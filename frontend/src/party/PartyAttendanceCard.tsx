import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  PARTY_ATTENDANCE_LIMITS,
  attendanceFiltersFor,
  checkInPartyGuest,
  codePoints,
  createPartyAttendanceGuest,
  deletePartyAttendanceGuest,
  getPartyAttendance,
  hasGuestList,
  normalizeText,
  undoPartyGuestCheckIn,
  unexpectedArrivals,
  updatePartyAttendanceGuest,
  visibleAttendance,
  type PartyAttendance,
  type PartyAttendanceFilter,
  type PartyAttendanceGuest,
  type PartyAttendanceOtherGuest,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { newClientRequestId } from './clientRequestId';

// WHO ARRIVED — the host's side of the door, inside the "Ospiti" tab.
//
// One screen for every party. With no guest list it is simply the people the
// host recorded ("Presenze registrate"), and it says plainly that this is not a
// head count: the party's QR admits anybody, and scanning it records nobody.
// With a guest list it shows who was expected, who has arrived, who has not,
// and everybody else who came — and a party with a list becomes a "mixed" one
// the moment somebody not on it is recorded, without any mode to switch.
//
// Invited guests can say "Sono qui" themselves from their own invitation; the
// host's buttons are the fallback and the correction. Nothing polls: the list
// is re-read when a mutation answers, when the guest list changes, and when the
// host asks for it.

const ERROR_KEYS: Record<string, MessageKey> = {
  attendance_not_open: 'party.attendance.error.attendance_not_open',
  version_conflict: 'party.attendance.error.version_conflict',
  invalid_name: 'party.attendance.error.invalid_name',
};

type Mutation = () => Promise<PartyAttendance>;

export function PartyAttendanceCard({
  partyId, refreshKey,
}: {
  partyId: string;
  /** Changes whenever the guest list does, so the people shown are the list's people. */
  refreshKey: string;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [attendance, setAttendance] = useState<PartyAttendance | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [reload, setReload] = useState(0);
  const [query, setQuery] = useState('');
  const [filter, setFilter] = useState<PartyAttendanceFilter>('all');
  const [busy, setBusy] = useState<Record<string, true>>({});
  const [notice, setNotice] = useState<string | null>(null);
  const [name, setName] = useState('');
  // ONE id per add, reused by that add's retries — so a double tap or a lost
  // answer names the person once. Typing a different name is a different add.
  const [requestId, setRequestId] = useState(newClientRequestId);
  const [editing, setEditing] = useState<{ id: string; name: string } | null>(null);
  const [confirmingRemove, setConfirmingRemove] = useState<string | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    setLoadFailed(false);
    getPartyAttendance(partyId, ctrl.signal)
      .then(setAttendance)
      .catch((err: unknown) => {
        if (ctrl.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setLoadFailed(true);
      });
    return () => ctrl.abort();
  }, [partyId, refreshKey, reload, invalidateAuth]);

  // The ONE place a refusal becomes what the host reads. A body carrying the
  // current attendance is adopted first; a row that vanished meanwhile is a
  // reason to read the list again, not an error to argue with.
  const refused = useCallback((err: unknown) => {
    if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
    const body = (err instanceof ApiError ? err.body : null) as
      { error?: string; attendance?: PartyAttendance } | null;
    // The state a refusal carries is newer than any open draft: adopt it and
    // close the editor, so the host reads the name as it now is.
    if (body?.attendance) {
      setAttendance(body.attendance);
      setEditing(null);
    }
    if (err instanceof ApiError && err.status === 404) setReload((n) => n + 1);
    setNotice(t((body?.error && ERROR_KEYS[body.error]) || 'party.attendance.error.generic'));
  }, [invalidateAuth, t]);

  const mutate = useCallback(async (key: string, call: Mutation): Promise<boolean> => {
    setBusy((b) => ({ ...b, [key]: true }));
    setNotice(null);
    try {
      setAttendance(await call());
      return true;
    } catch (err) {
      refused(err);
      return false;
    } finally {
      setBusy((b) => { const next = { ...b }; delete next[key]; return next; });
    }
  }, [refused]);

  if (loadFailed) {
    return (
      <section className="party-card" data-testid="party-attendance">
        <p className="inline-error" role="alert">{t('party.attendance.loadError')}</p>
      </section>
    );
  }
  if (!attendance) {
    return <section className="party-card" data-testid="party-attendance"><p role="status">{t('common.loading')}</p></section>;
  }

  const withList = hasGuestList(attendance);
  const filters = attendanceFiltersFor(attendance);
  const shown = visibleAttendance(attendance, query, filters.includes(filter) ? filter : 'all');
  const summary = attendance.summary;
  const canEdit = attendance.canEdit;
  const time = (iso: string) => formatDate(iso, { timeStyle: 'short' });
  const nameValid = normalizeText(name) !== null && codePoints(name.trim()) <= PARTY_ATTENDANCE_LIMITS.name;
  const anybody = attendance.groups.length > 0 || attendance.otherGuests.length > 0;

  async function add() {
    if (!nameValid || busy.add) return;
    const ok = await mutate('add', () => createPartyAttendanceGuest(partyId, { name: name.trim(), clientRequestId: requestId }));
    if (ok) {
      setName('');
      setRequestId(newClientRequestId());
    }
  }

  async function saveEdit(other: PartyAttendanceOtherGuest) {
    if (!editing || normalizeText(editing.name) === null) return;
    const ok = await mutate(`other:${other.id}`, () =>
      updatePartyAttendanceGuest(partyId, other.id, { name: editing.name.trim(), version: other.version }));
    if (ok) setEditing(null);
  }

  const guestRow = (guest: PartyAttendanceGuest) => {
    const key = `guest:${guest.guestId}`;
    return (
      <li
        key={guest.guestId} className="party-attendance-person"
        data-testid={`party-attendance-guest-${guest.guestId}`} data-arrived={guest.checkedInAt !== null}
      >
        <div className="party-attendance-who">
          <span className="party-attendance-name">
            {guest.name}
            {guest.isAdditionalGuest && <span className="party-guests-chip">+1</span>}
          </span>
          <span className="party-attendance-state muted">
            {t(`party.attendance.rsvp.${guest.rsvpStatus}` as MessageKey)}
            {' · '}
            {guest.checkedInAt
              ? t('party.attendance.arrivedAt', { time: time(guest.checkedInAt) })
              : t('party.attendance.notArrived')}
            {guest.checkInSource === 'invitation' && <> · {t('party.attendance.bySelf')}</>}
          </span>
        </div>
        {canEdit && (guest.checkedInAt === null ? (
          <button
            type="button" className="row-action-primary" data-testid={`party-attendance-checkin-${guest.guestId}`}
            disabled={Boolean(busy[key])}
            onClick={() => void mutate(key, () => checkInPartyGuest(partyId, guest.guestId))}
          >
            {t('party.attendance.checkIn')}
          </button>
        ) : (
          <button
            type="button" className="row-action" data-testid={`party-attendance-undo-${guest.guestId}`}
            disabled={Boolean(busy[key])}
            onClick={() => void mutate(key, () => undoPartyGuestCheckIn(partyId, guest.guestId))}
          >
            {t('party.attendance.undo')}
          </button>
        ))}
      </li>
    );
  };

  const otherRow = (other: PartyAttendanceOtherGuest) => {
    const key = `other:${other.id}`;
    const isEditing = editing?.id === other.id;
    return (
      <li key={other.id} className="party-attendance-person" data-testid={`party-attendance-other-${other.id}`} data-arrived>
        {isEditing ? (
          <form
            className="party-attendance-edit"
            onSubmit={(e) => { e.preventDefault(); void saveEdit(other); }}
          >
            <input
              value={editing.name} disabled={Boolean(busy[key])} aria-label={t('party.attendance.addName')}
              data-testid={`party-attendance-other-name-${other.id}`}
              onChange={(e) => setEditing({ id: other.id, name: e.target.value })}
            />
            <button
              type="submit" className="row-action-primary" data-testid={`party-attendance-other-save-${other.id}`}
              disabled={Boolean(busy[key]) || normalizeText(editing.name) === null}
            >
              {t('party.attendance.save')}
            </button>
            <button type="button" className="row-action" onClick={() => setEditing(null)}>
              {t('party.attendance.cancel')}
            </button>
          </form>
        ) : (
          <>
            <div className="party-attendance-who">
              <span className="party-attendance-name">{other.name}</span>
              <span className="party-attendance-state muted">
                {t('party.attendance.arrivedAt', { time: time(other.checkedInAt) })}
              </span>
            </div>
            {canEdit && (
              <span className="party-attendance-actions">
                <button
                  type="button" className="row-action" data-testid={`party-attendance-other-edit-${other.id}`}
                  disabled={Boolean(busy[key])}
                  onClick={() => { setNotice(null); setEditing({ id: other.id, name: other.name }); }}
                >
                  {t('party.attendance.edit')}
                </button>
                <button
                  type="button" className="row-action row-action-danger"
                  data-testid={`party-attendance-other-remove-${other.id}`} disabled={Boolean(busy[key])}
                  onClick={() => setConfirmingRemove(other.id)}
                >
                  {t('party.attendance.remove')}
                </button>
              </span>
            )}
          </>
        )}
        {confirmingRemove === other.id && (
          <div className="party-teardown-confirm" data-testid={`party-attendance-other-confirm-${other.id}`}>
            <p role="alert">{t('party.attendance.removeConfirm', { name: other.name })}</p>
            <button
              type="button" className="row-action row-action-danger"
              data-testid={`party-attendance-other-confirm-yes-${other.id}`}
              onClick={() => { setConfirmingRemove(null); void mutate(key, () => deletePartyAttendanceGuest(partyId, other.id)); }}
            >
              {t('party.attendance.removeYes')}
            </button>
            <button type="button" className="row-action" onClick={() => setConfirmingRemove(null)}>
              {t('party.attendance.cancel')}
            </button>
          </div>
        )}
      </li>
    );
  };

  return (
    <section
      className="party-card" data-testid="party-attendance" data-guest-list={withList}
      aria-labelledby="party-attendance-heading"
    >
      <div className="party-guests-row-head">
        <h3 id="party-attendance-heading">
          {withList ? t('party.attendance.heading') : t('party.attendance.recordedHeading')}
        </h3>
        <button type="button" className="row-action" data-testid="party-attendance-refresh" onClick={() => setReload((n) => n + 1)}>
          {t('party.attendance.refresh')}
        </button>
      </div>

      {withList ? (
        <>
          <dl className="party-guests-metrics" data-testid="party-attendance-metrics">
            {([
              ['expected', summary.expectedPeople],
              ['arrived', summary.totalArrivals],
              ['missing', summary.expectedMissing],
              ['others', unexpectedArrivals(summary)],
            ] as const).map(([key, value]) => (
              <div key={key} className="party-guests-metric" data-metric={key}>
                <dt>{t(`party.attendance.metric.${key}` as MessageKey)}</dt>
                <dd>{value}</dd>
              </div>
            ))}
          </dl>
          {summary.unexpectedKnownGuests > 0 && (
            <p className="muted" data-testid="party-attendance-unexpected-known">
              {t('party.attendance.unexpectedKnown', { count: summary.unexpectedKnownGuests })}
            </p>
          )}
          {attendance.partyStatus === 'live' && <p className="muted">{t('party.attendance.selfCheckInNote')}</p>}
        </>
      ) : (
        <>
          <dl className="party-guests-metrics" data-testid="party-attendance-metrics">
            <div className="party-guests-metric" data-metric="recorded">
              <dt>{t('party.attendance.metric.recorded')}</dt>
              <dd>{summary.totalArrivals}</dd>
            </div>
          </dl>
          <p className="muted" data-testid="party-attendance-recorded-note">{t('party.attendance.recordedNote')}</p>
        </>
      )}
      {attendance.partyStatus === 'ended' && canEdit && (
        <p className="muted" data-testid="party-attendance-ended">{t('party.attendance.endedNote')}</p>
      )}

      {canEdit && (
        <form
          className="party-attendance-add" data-testid="party-attendance-add-form"
          onSubmit={(e) => { e.preventDefault(); void add(); }}
        >
          <input
            value={name} placeholder={t('party.attendance.addName')} aria-label={t('party.attendance.addName')}
            data-testid="party-attendance-add-name" disabled={Boolean(busy.add)}
            onChange={(e) => { setName(e.target.value); setRequestId(newClientRequestId()); }}
          />
          <button
            type="submit" className="row-action-primary" data-testid="party-attendance-add"
            disabled={Boolean(busy.add) || !nameValid}
          >
            {t('party.attendance.add')}
          </button>
        </form>
      )}

      {notice && <p className="inline-error" role="alert" data-testid="party-attendance-notice">{notice}</p>}

      {anybody && (
        <div className="party-guests-toolbar">
          <label className="party-field party-guests-search">
            <span className="visually-hidden">{t('party.attendance.search')}</span>
            <input
              type="search" value={query} placeholder={t('party.attendance.search')}
              aria-label={t('party.attendance.search')} data-testid="party-attendance-search"
              onChange={(e) => setQuery(e.target.value)}
            />
          </label>
          {filters.length > 0 && (
            <div className="party-attendance-filters" role="group" aria-label={t('party.attendance.filters')}>
              {filters.map((f) => (
                <button
                  key={f} type="button" className="row-action" aria-pressed={filter === f}
                  data-testid={`party-attendance-filter-${f}`} onClick={() => setFilter(f)}
                >
                  {t(`party.attendance.filter.${f}` as MessageKey)}
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      {!anybody ? (
        <p className="muted" data-testid="party-attendance-empty">{t('party.attendance.recorded.empty')}</p>
      ) : shown.groups.length === 0 && shown.otherGuests.length === 0 ? (
        <p className="muted" data-testid="party-attendance-no-matches">{t('party.attendance.noMatches')}</p>
      ) : (
        <>
          {shown.groups.length > 0 && (
            <ul className="party-guests-list">
              {shown.groups.map((group) => (
                <li key={group.groupId} className="party-guests-row" data-testid={`party-attendance-group-${group.groupId}`}>
                  <strong className="party-guests-label">{group.label}</strong>
                  <ul className="party-attendance-people">{group.guests.map(guestRow)}</ul>
                </li>
              ))}
            </ul>
          )}
          {shown.otherGuests.length > 0 && (
            <div data-testid="party-attendance-others">
              {withList && <h4 className="party-attendance-subheading">{t('party.attendance.others.heading')}</h4>}
              <ul className="party-attendance-people">{shown.otherGuests.map(otherRow)}</ul>
            </div>
          )}
        </>
      )}
    </section>
  );
}

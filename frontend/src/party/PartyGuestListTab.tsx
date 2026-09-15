import { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ApiError,
  PARTY_INVITATION_LIMITS,
  createPartyInvitationGroup,
  deletePartyInvitationGroup,
  getPartyGuestList,
  isPlausibleEmail,
  matchesGuestSearch,
  normalizeText,
  remindPartyInvitation,
  rotatePartyInvitationLink,
  sendPartyInvitation,
  updatePartyInvitationGroup,
  type Party,
  type PartyGuestList,
  type PartyInvitationGroup,
  type PartyInvitationGroupWrite,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type MessageKey } from '../i18n';
import { newClientRequestId } from './clientRequestId';
import { PartyAttendanceCard } from './PartyAttendanceCard';
import { PartyRsvpQuestionsCard } from './PartyRsvpQuestionsCard';
import './PartyGuestList.css';

// The host's GUEST LIST — the Before half of the party, on the party's own
// workspace rather than in an administration app of its own.
//
// Everything here is owner-private: names, addresses, phone numbers, notes,
// answers. The server answers every write with the whole list, so the numbers
// at the top always describe the rows below them, and a refusal that describes
// a state — somebody replied meanwhile, the party has started — carries the
// state, which the page adopts instead of arguing with it.

type Busy = 'send' | 'remind' | 'rotate' | 'remove';

type Confirming = { groupId: string; action: 'remove' | 'rotate' } | null;

const ERROR_KEYS: Record<string, MessageKey> = {
  version_conflict: 'party.guests.error.version_conflict',
  party_version_conflict: 'party.guests.error.party_version_conflict',
  invitations_closed: 'party.guests.error.invitations_closed',
  reminder_not_allowed: 'party.guests.error.reminder_not_allowed',
  mail_unavailable: 'party.guests.mailUnavailable',
  additional_guests_in_use: 'party.guests.editor.error.additional_guests_in_use',
};

export function PartyGuestListTab({
  party, onPartyUpdated,
}: {
  party: Party;
  onPartyUpdated(next: Party): void;
}) {
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [list, setList] = useState<PartyGuestList | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [query, setQuery] = useState('');
  const [editing, setEditing] = useState<PartyInvitationGroup | 'new' | null>(null);
  const [busy, setBusy] = useState<Record<string, Busy>>({});
  const [confirming, setConfirming] = useState<Confirming>(null);
  const [notice, setNotice] = useState<{ tone: 'ok' | 'error'; text: string } | null>(null);

  useEffect(() => {
    const ctrl = new AbortController();
    setLoadFailed(false);
    getPartyGuestList(party.id, ctrl.signal)
      .then(setList)
      .catch((err: unknown) => {
        if (ctrl.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setLoadFailed(true);
      });
    return () => ctrl.abort();
  }, [party.id, invalidateAuth]);

  // The ONE place a refusal becomes what the host reads. A body carrying the
  // current list or party is adopted before anything is said about it.
  const refused = useCallback((err: unknown, fallback: MessageKey) => {
    if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
    const body = (err instanceof ApiError ? err.body : null) as
      { error?: string; guestList?: PartyGuestList; party?: Party } | null;
    if (body?.guestList) setList(body.guestList);
    if (body?.party) onPartyUpdated(body.party);
    setNotice({ tone: 'error', text: t((body?.error && ERROR_KEYS[body.error]) || fallback) });
  }, [invalidateAuth, onPartyUpdated, t]);

  const run = useCallback(async (group: PartyInvitationGroup, action: Busy) => {
    setBusy((b) => ({ ...b, [group.id]: action }));
    setNotice(null);
    setConfirming(null);
    try {
      if (action === 'send' || action === 'remind') {
        // A NEW id per click, sent once. A retry of this click would reuse it
        // and the server would answer with the first attempt, never a second
        // email; pressing again is a new click and a new decision.
        const clientRequestId = newClientRequestId();
        const result = action === 'send'
          ? await sendPartyInvitation(party.id, group.id, { clientRequestId, partyVersion: party.version })
          : await remindPartyInvitation(party.id, group.id, clientRequestId);
        setList(result.guestList);
        onPartyUpdated(result.party);
        const status = result.delivery.status;
        setNotice(status === 'sent'
          ? { tone: 'ok', text: action === 'remind' ? t('party.guests.reminded') : t('party.guests.sent', { label: group.label }) }
          : { tone: 'error', text: status === 'failed' ? t('party.guests.sendFailed') : t('party.guests.delivery.pending') });
      } else if (action === 'rotate') {
        setList(await rotatePartyInvitationLink(party.id, group.id, group.version));
        setNotice({ tone: 'ok', text: t('party.guests.rotated') });
      } else {
        setList(await deletePartyInvitationGroup(party.id, group.id, group.version));
      }
    } catch (err) {
      refused(err, 'party.guests.error.generic');
    } finally {
      setBusy((b) => { const next = { ...b }; delete next[group.id]; return next; });
    }
  }, [party.id, party.version, onPartyUpdated, refused, t]);

  const visible = useMemo(
    () => (list ? list.groups.filter((g) => matchesGuestSearch(g, query)) : []),
    [list, query],
  );

  if (loadFailed) {
    return <p className="inline-error" role="alert">{t('party.guests.loadError')}</p>;
  }
  if (!list) {
    return <p role="status">{t('common.loading')}</p>;
  }

  const summary = list.summary;
  const questionPrompt = (id: string) => list.questions.find((q) => q.id === id);

  // NO PARTY "TYPE". A party with no invitation is an open party — the QR
  // admits anybody — and the guest list is an optional tool it may pick up;
  // a party with a list becomes a mixed one the moment somebody not on it is
  // recorded at the door. Which of these a party is follows from the rows.
  const openParty = list.groups.length === 0;
  const attendanceOpen = party.status === 'live' || party.status === 'ended';
  // While the party is on (and after), arriving is what the tab is for, and the
  // invitation machinery folds away beneath it; an open party keeps its
  // optional guest list folded in every phase.
  const folded = openParty || attendanceOpen;
  // The arrivals are re-read whenever the list they are drawn from changes.
  const refreshKey = list.groups.map((g) => `${g.id}:${g.version}`).join(',');

  const management = (
    <>
      {!openParty && (
      <section className="party-card" aria-labelledby="party-guests-heading">
        <h3 id="party-guests-heading">{t('party.guests.heading')}</h3>
        <dl className="party-guests-metrics" data-testid="party-guests-metrics">
          {([
            ['invited', summary.invited],
            ['missing', summary.missingResponses],
            ['attending', summary.attending],
            ['expected', summary.expectedPeople],
          ] as const).map(([key, value]) => (
            <div key={key} className="party-guests-metric" data-metric={key}>
              <dt>{t(`party.guests.metric.${key}` as MessageKey)}</dt>
              <dd>{value}</dd>
            </div>
          ))}
        </dl>
        <p className="muted" data-testid="party-guests-declined">
          {t('party.guests.metric.declined', { count: summary.declined })}
        </p>
        {!list.mailAvailable && (
          <p className="muted" role="note" data-testid="party-guests-mail-unavailable">{t('party.guests.mailUnavailable')}</p>
        )}
        {list.mailAvailable && party.status === 'draft' && (
          <p className="muted">{t('party.guests.publishNote')}</p>
        )}
      </section>
      )}
      {openParty && !list.mailAvailable && (
        <p className="muted" role="note" data-testid="party-guests-mail-unavailable">{t('party.guests.mailUnavailable')}</p>
      )}

      <section className="party-card">
        <div className="party-guests-toolbar">
          <label className="party-field party-guests-search">
            <span className="visually-hidden">{t('party.guests.search')}</span>
            <input
              type="search" value={query} placeholder={t('party.guests.search')}
              aria-label={t('party.guests.search')} data-testid="party-guests-search"
              onChange={(e) => setQuery(e.target.value)}
            />
          </label>
          <button
            type="button" className="row-action-primary" data-testid="party-guests-add"
            onClick={() => { setNotice(null); setEditing('new'); }}
          >
            {t('party.guests.add')}
          </button>
        </div>

        {notice && (
          <p
            className={notice.tone === 'error' ? 'inline-error' : 'muted'}
            role={notice.tone === 'error' ? 'alert' : 'status'} data-testid="party-guests-notice"
          >
            {notice.text}
          </p>
        )}

        {editing && (
          <PartyInvitationGroupEditor
            partyId={party.id}
            group={editing === 'new' ? null : editing}
            onSaved={(next) => { setList(next); setEditing(null); }}
            onRefused={(err) => { refused(err, 'party.guests.editor.error.generic'); }}
            onCancel={() => setEditing(null)}
          />
        )}

        {list.groups.length === 0 ? (
          <p className="muted" data-testid="party-guests-empty">{t('party.guests.empty')}</p>
        ) : visible.length === 0 ? (
          <p className="muted">{t('party.guests.noMatches')}</p>
        ) : (
          <ul className="party-guests-list">
            {visible.map((group) => {
              const inFlight = busy[group.id];
              const sentBefore = group.delivery.state === 'sent';
              const people = group.guests.filter((g) => !g.isAdditionalGuest).length + group.additionalGuestsUsed;
              return (
                <li key={group.id} className="party-guests-row" data-testid={`party-group-${group.id}`}>
                  <div className="party-guests-row-head">
                    <strong className="party-guests-label">{group.label}</strong>
                    <span className="party-guests-delivery" data-state={inFlight === 'send' || inFlight === 'remind' ? 'sending' : group.delivery.state}>
                      {inFlight === 'send' || inFlight === 'remind'
                        ? t('party.guests.delivery.sending')
                        : t(`party.guests.delivery.${group.delivery.state}` as MessageKey)}
                    </span>
                  </div>
                  <p className="party-guests-contact muted">
                    {group.recipientEmail}{group.phone ? ` · ${group.phone}` : ''}
                  </p>
                  <ul className="party-guests-people">
                    {group.guests.map((g) => (
                      <li key={g.id} data-status={g.status}>
                        <span>{g.name}</span>
                        {g.isAdditionalGuest && <span className="party-guests-chip">{t('party.guests.plusOne')}</span>}
                        <span className="party-guests-chip" data-status={g.status}>
                          {t(`party.guests.status.${g.status}` as MessageKey)}
                        </span>
                      </li>
                    ))}
                  </ul>
                  <p className="party-guests-counts muted">
                    {t('party.guests.row.counts', { attending: group.attendingCount, pending: group.pendingCount, people })}
                    {group.maxAdditionalGuests > 0 && (
                      <> · {t('party.guests.plusOnes', { used: group.additionalGuestsUsed, max: group.maxAdditionalGuests })}</>
                    )}
                  </p>
                  {group.delivery.lastSentAt && (
                    <p className="muted">{t('party.guests.delivery.lastSent', { date: formatDate(group.delivery.lastSentAt) })}</p>
                  )}
                  {sentBefore && group.delivery.lastAttemptStatus === 'failed' && (
                    <p className="muted">{t('party.guests.delivery.lastFailed')}</p>
                  )}

                  <p className="party-card-actions">
                    {group.canSend && (
                      <button
                        type="button" className="row-action" data-testid={`party-group-send-${group.id}`}
                        disabled={Boolean(inFlight)} onClick={() => void run(group, 'send')}
                      >
                        {sentBefore ? t('party.guests.action.resend') : t('party.guests.action.send')}
                      </button>
                    )}
                    {group.canRemind && (
                      <button
                        type="button" className="row-action" data-testid={`party-group-remind-${group.id}`}
                        disabled={Boolean(inFlight)} onClick={() => void run(group, 'remind')}
                      >
                        {t('party.guests.action.remind')}
                      </button>
                    )}
                    <button
                      type="button" className="row-action" data-testid={`party-group-edit-${group.id}`}
                      disabled={Boolean(inFlight)} onClick={() => { setNotice(null); setEditing(group); }}
                    >
                      {t('party.guests.action.edit')}
                    </button>
                    <button
                      type="button" className="row-action row-action-danger" data-testid={`party-group-remove-${group.id}`}
                      disabled={Boolean(inFlight)} onClick={() => setConfirming({ groupId: group.id, action: 'remove' })}
                    >
                      {t('party.guests.action.remove')}
                    </button>
                  </p>

                  {confirming?.groupId === group.id && (
                    <div className="party-teardown-confirm" data-testid={`party-group-confirm-${group.id}`}>
                      <p role="alert">
                        {confirming.action === 'remove'
                          ? t('party.guests.removeConfirm', { label: group.label })
                          : t('party.guests.rotateHelp')}
                      </p>
                      <button
                        type="button" className="row-action row-action-danger"
                        data-testid={`party-group-confirm-yes-${group.id}`}
                        onClick={() => void run(group, confirming.action)}
                      >
                        {confirming.action === 'remove' ? t('party.guests.removeYes') : t('party.guests.rotateConfirm')}
                      </button>
                      <button type="button" className="row-action" onClick={() => setConfirming(null)}>
                        {t('party.guests.cancel')}
                      </button>
                    </div>
                  )}

                  {(group.answers.length > 0 || group.guests.some((g) => g.dietaryNotes)) && (
                    <details className="party-advanced" data-testid={`party-group-answers-${group.id}`}>
                      <summary>{t('party.guests.answers')}</summary>
                      <dl className="party-guests-answers">
                        {group.guests.filter((g) => g.dietaryNotes).map((g) => (
                          <div key={g.id}>
                            <dt>{t('party.guests.dietaryOf', { name: g.name })}</dt>
                            <dd>{g.dietaryNotes}</dd>
                          </div>
                        ))}
                        {group.answers.map((a) => {
                          const question = questionPrompt(a.questionId);
                          return (
                            <div key={a.questionId}>
                              <dt>
                                {question?.prompt}
                                {question && !question.isActive && <> {t('party.guests.retired')}</>}
                              </dt>
                              <dd>{typeof a.value === 'boolean' ? (a.value ? t('partyRsvp.yes') : t('partyRsvp.no')) : a.value}</dd>
                            </div>
                          );
                        })}
                      </dl>
                    </details>
                  )}

                  {/* Replacing the personal link is deliberate and destructive, so
                      it is folded away rather than beside the everyday actions. */}
                  <details className="party-advanced">
                    <summary>{t('party.guests.action.link')}</summary>
                    <p className="muted">{t('party.guests.rotateHelp')}</p>
                    <button
                      type="button" className="row-action row-action-danger"
                      data-testid={`party-group-rotate-${group.id}`} disabled={Boolean(inFlight)}
                      onClick={() => setConfirming({ groupId: group.id, action: 'rotate' })}
                    >
                      {t('party.guests.action.rotate')}
                    </button>
                  </details>
                </li>
              );
            })}
          </ul>
        )}
      </section>

      <PartyRsvpQuestionsCard
        partyId={party.id}
        questions={list.questions}
        onChanged={setList}
        onRefused={(err, fallback) => refused(err, fallback)}
      />
    </>
  );

  return (
    <div className="party-overview" data-testid="party-guests">
      {attendanceOpen && <PartyAttendanceCard partyId={party.id} refreshKey={refreshKey} />}
      {!attendanceOpen && openParty && (
        <section className="party-card" data-testid="party-guests-open" aria-labelledby="party-guests-open-heading">
          <h3 id="party-guests-open-heading">{t('party.guests.open.heading')}</h3>
          <p>{t('party.guests.open.body')}</p>
          <p className="muted">{t('party.guests.open.attendanceLater')}</p>
        </section>
      )}
      {folded ? (
        <details className="party-advanced party-guests-manage" data-testid="party-guests-manage">
          <summary>{openParty ? t('party.guests.optional.summary') : t('party.guests.manage.summary')}</summary>
          {openParty && <p className="muted">{t('party.guests.optional.help')}</p>}
          {management}
        </details>
      ) : management}
    </div>
  );
}

interface PersonDraft { key: string; id?: string; name: string; email: string; phone: string }

let personKey = 0;
const blankPerson = (): PersonDraft => ({ key: `p-${(personKey += 1)}`, name: '', email: '', phone: '' });

function PartyInvitationGroupEditor({
  partyId, group, onSaved, onRefused, onCancel,
}: {
  partyId: string;
  group: PartyInvitationGroup | null;
  onSaved(next: PartyGuestList): void;
  onRefused(err: unknown): void;
  onCancel(): void;
}) {
  const { t } = useI18n();
  const [label, setLabel] = useState(group?.label ?? '');
  const [email, setEmail] = useState(group?.recipientEmail ?? '');
  const [phone, setPhone] = useState(group?.phone ?? '');
  const [maxAdditional, setMaxAdditional] = useState(group?.maxAdditionalGuests ?? 0);
  const [people, setPeople] = useState<PersonDraft[]>(() => group
    ? group.guests.filter((g) => !g.isAdditionalGuest).map((g) => ({
      key: g.id, id: g.id, name: g.name, email: g.email ?? '', phone: g.phone ?? '',
    }))
    : [blankPerson()]);
  const [saving, setSaving] = useState(false);

  const emailChanged = group !== null
    && email.trim().toLowerCase() !== group.recipientEmail.toLowerCase();
  const valid = normalizeText(label) !== null
    && isPlausibleEmail(email)
    && people.length > 0
    && people.length <= PARTY_INVITATION_LIMITS.namedGuests
    && people.every((p) => normalizeText(p.name) !== null && (p.email.trim() === '' || isPlausibleEmail(p.email)))
    && maxAdditional >= 0 && maxAdditional <= PARTY_INVITATION_LIMITS.additionalGuests;

  // A person, a couple, a family: a starting SHAPE, never a type the server
  // stores. Names already typed are kept.
  const shape = (count: number) => setPeople((current) =>
    Array.from({ length: count }, (_, i) => current[i] ?? blankPerson()));

  async function save() {
    if (!valid) return;
    const body: PartyInvitationGroupWrite = {
      label: label.trim(),
      recipientEmail: email.trim(),
      phone: normalizeText(phone),
      maxAdditionalGuests: maxAdditional,
      guests: people.map((p) => ({
        ...(p.id ? { id: p.id } : {}),
        name: p.name.trim(),
        email: normalizeText(p.email),
        phone: normalizeText(p.phone),
      })),
      version: group?.version ?? 0,
    };
    setSaving(true);
    try {
      onSaved(group
        ? await updatePartyInvitationGroup(partyId, group.id, body)
        : await createPartyInvitationGroup(partyId, body));
    } catch (err) {
      onRefused(err);
    } finally {
      setSaving(false);
    }
  }

  return (
    <form
      className="party-guests-editor" data-testid="party-group-editor"
      onSubmit={(e) => { e.preventDefault(); void save(); }}
    >
      <h4>{group ? t('party.guests.editor.editTitle') : t('party.guests.editor.createTitle')}</h4>
      {!group && (
        <p className="party-card-actions" role="group" aria-label={t('party.guests.editor.shape')}>
          {([['person', 1], ['couple', 2], ['family', 3]] as const).map(([key, count]) => (
            <button
              key={key} type="button" className="row-action" data-testid={`party-group-shape-${key}`}
              onClick={() => shape(count)}
            >
              {t(`party.guests.editor.shape.${key}` as MessageKey)}
            </button>
          ))}
        </p>
      )}
      <label className="party-field">
        <span>{t('party.guests.editor.label')}</span>
        <input
          value={label} disabled={saving} aria-label={t('party.guests.editor.label')}
          placeholder={t('party.guests.editor.labelHelp')} onChange={(e) => setLabel(e.target.value)}
        />
      </label>
      <label className="party-field">
        <span>{t('party.guests.editor.email')}</span>
        <input
          type="email" value={email} disabled={saving} aria-label={t('party.guests.editor.email')}
          onChange={(e) => setEmail(e.target.value)}
        />
      </label>
      <p className="muted">{t('party.guests.editor.emailHelp')}</p>
      {emailChanged && (
        <p className="inline-error" role="note" data-testid="party-group-email-changed">
          {t('party.guests.editor.emailChanged')}
        </p>
      )}
      <label className="party-field">
        <span>{t('party.guests.editor.phone')}</span>
        <input
          type="tel" value={phone} disabled={saving} aria-label={t('party.guests.editor.phone')}
          onChange={(e) => setPhone(e.target.value)}
        />
      </label>
      <label className="party-number">
        <span>{t('party.guests.editor.maxAdditional')}</span>
        <input
          type="number" min={0} max={PARTY_INVITATION_LIMITS.additionalGuests} value={maxAdditional}
          disabled={saving} aria-label={t('party.guests.editor.maxAdditional')}
          onChange={(e) => setMaxAdditional(Number(e.target.value))}
        />
      </label>

      <fieldset className="party-guests-people-editor">
        <legend>{t('party.guests.editor.people')}</legend>
        {people.map((person, index) => (
          <div key={person.key} className="party-guests-person" data-testid="party-group-person">
            <input
              value={person.name} disabled={saving}
              aria-label={`${t('party.guests.editor.personName')} ${index + 1}`}
              placeholder={t('party.guests.editor.personName')}
              onChange={(e) => setPeople((ps) => ps.map((p) => (p.key === person.key ? { ...p, name: e.target.value } : p)))}
            />
            <input
              type="email" value={person.email} disabled={saving}
              aria-label={`${t('party.guests.editor.personEmail')} ${index + 1}`}
              placeholder={t('party.guests.editor.personEmail')}
              onChange={(e) => setPeople((ps) => ps.map((p) => (p.key === person.key ? { ...p, email: e.target.value } : p)))}
            />
            <input
              type="tel" value={person.phone} disabled={saving}
              aria-label={`${t('party.guests.editor.personPhone')} ${index + 1}`}
              placeholder={t('party.guests.editor.personPhone')}
              onChange={(e) => setPeople((ps) => ps.map((p) => (p.key === person.key ? { ...p, phone: e.target.value } : p)))}
            />
            {people.length > 1 && (
              <button
                type="button" className="row-action" disabled={saving}
                aria-label={t('party.guests.editor.removePerson', { name: person.name || String(index + 1) })}
                onClick={() => setPeople((ps) => ps.filter((p) => p.key !== person.key))}
              >
                ×
              </button>
            )}
          </div>
        ))}
        {people.length < PARTY_INVITATION_LIMITS.namedGuests && (
          <button
            type="button" className="row-action" data-testid="party-group-add-person" disabled={saving}
            onClick={() => setPeople((ps) => [...ps, blankPerson()])}
          >
            {t('party.guests.editor.addPerson')}
          </button>
        )}
      </fieldset>
      {group && group.additionalGuestsUsed > 0 && (
        <p className="muted">{t('party.guests.editor.plusOnesKept', { count: group.additionalGuestsUsed })}</p>
      )}

      {!valid && <p className="muted">{t('party.guests.editor.invalid')}</p>}
      <p className="party-card-actions">
        <button
          type="submit" className="row-action-primary" data-testid="party-group-save" disabled={saving || !valid}
        >
          {t('party.guests.editor.save')}
        </button>
        <button type="button" className="row-action" disabled={saving} onClick={onCancel}>
          {t('party.guests.cancel')}
        </button>
      </p>
    </form>
  );
}

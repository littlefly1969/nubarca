import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams } from 'react-router';
import {
  ApiError,
  getAlbumPartySettings,
  getParty,
  listPartyGuestContent,
  transitionParty,
  updateParty,
  type AlbumPartyStatus,
  type Party,
  type PartyGuestContentKind,
  type PartyGuestContentSlot,
  type PartyLifecycleAction,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { PartyAlbumSection } from '../party/PartyAlbumSection';
import { PartyGuestAccessSection } from '../party/PartyGuestAccessSection';
import { PartyLiveTab } from '../party/PartyLiveTab';
import { PartyContentCard } from '../party/PartyContentEditors';
import {
  PARTY_TIMELINE,
  mainMediaSource,
  partyPrimaryAction,
  partyStatusLabelKey,
  timelineStepState,
} from '../party/partyModel';
import '../party/Party.css';

// The host's own surface for ONE party.
//
// Three tabs, and deliberately only three: Overview, Live and Photos. There are
// no empty "Before" and "After" tabs waiting to be filled — a tab that promises
// something the product cannot yet do is worse than no tab, and the slice that
// gives them content will add them.
//
// Everything below the party root is reached through its MAIN ALBUM, using the
// functions that already work. This page resolves that album once and hands the
// id down; nothing here mints a token, moderates a photograph or configures a
// printer of its own.

// Five tabs, and every one of them has content. There were three until the
// party had something to say beforehand and afterwards; a tab that promises
// what the product cannot do is worse than no tab.
type Tab = 'overview' | 'before' | 'live' | 'after' | 'photos';

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; party: Party }
  | { kind: 'missing' }
  | { kind: 'error' };

// A datetime-local input speaks local wall time with no zone; the wire speaks
// UTC. Converting in one place at each boundary is what keeps "ends at 23:00"
// from drifting an hour every time the form is opened.
function toLocalInput(iso: string | null): string {
  if (!iso) return '';
  const at = new Date(iso);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}`
    + `T${pad(at.getHours())}:${pad(at.getMinutes())}`;
}

function fromLocalInput(value: string): string | null {
  return value === '' ? null : new Date(value).toISOString();
}

export function PartyWorkspacePage() {
  const { partyId } = useParams<{ partyId: string }>();
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [tab, setTab] = useState<Tab>('overview');
  const [albumParty, setAlbumParty] = useState<AlbumPartyStatus | null>(null);
  // Every kind, always — the server returns the ones the host has written and
  // the ones they have not, so the editor renders what the server says rather
  // than holding a second opinion about the defaults.
  const [contentSlots, setContentSlots] = useState<PartyGuestContentSlot[]>([]);
  const [libraryDraft, setLibraryDraft] = useState('');
  const [librarySaving, setLibrarySaving] = useState(false);
  const abortRef = useRef<AbortController | null>(null);

  const onSlotSaved = useCallback((next: PartyGuestContentSlot) => {
    setContentSlots((cur) => cur.map((slot) => (slot.kind === next.kind ? next : slot)));
  }, []);

  // The memories' window rides on the party's OWN metadata mutation — it is the
  // party's data and shares the party's version, so there is no second endpoint
  // and no second concurrency check for one date.
  const saveLibraryWindow = useCallback(async () => {
    if (status.kind !== 'ready') return;
    setLibrarySaving(true);
    try {
      const next = await updateParty(status.party.id, {
        title: status.party.title,
        description: status.party.description,
        eventStartsAt: status.party.eventStartsAt,
        guestAccessExpiresAt: status.party.guestAccessExpiresAt,
        libraryAccessExpiresAt: fromLocalInput(libraryDraft),
        version: status.party.version,
      });
      setStatus({ kind: 'ready', party: next });
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) invalidateAuth();
    } finally { setLibrarySaving(false); }
  }, [status, libraryDraft, invalidateAuth]);

  const load = useCallback((signal: AbortSignal) => {
    if (!partyId) return;
    getParty(partyId, signal)
      .then((party) => setStatus({ kind: 'ready', party }))
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus(err instanceof ApiError && err.status === 404
          ? { kind: 'missing' } : { kind: 'error' });
      });
  }, [partyId, invalidateAuth]);

  useEffect(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setStatus({ kind: 'loading' });
    load(ctrl.signal);
    return () => ctrl.abort();
  }, [load]);

  useEffect(() => {
    if (!partyId) return;
    const ctrl = new AbortController();
    listPartyGuestContent(partyId, ctrl.signal)
      .then(setContentSlots)
      .catch(() => { if (!ctrl.signal.aborted) setContentSlots([]); });
    return () => ctrl.abort();
  }, [partyId]);

  useEffect(() => {
    if (status.kind !== 'ready') return;
    setLibraryDraft(toLocalInput(status.party.libraryAccessExpiresAt));
  }, [status.kind, status.kind === 'ready' ? status.party.version : 0]);

  const main = status.kind === 'ready' ? mainMediaSource(status.party) : null;
  const mainAlbumId = main?.albumId ?? null;

  // The album's party settings are asked for ONLY when there is an album to ask
  // about. A party with no album yet must never send an album-scoped request
  // with an invented id.
  useEffect(() => {
    if (!mainAlbumId) { setAlbumParty(null); return; }
    const ctrl = new AbortController();
    getAlbumPartySettings(mainAlbumId, ctrl.signal)
      .then(setAlbumParty)
      .catch(() => { if (!ctrl.signal.aborted) setAlbumParty(null); });
    return () => ctrl.abort();
  }, [mainAlbumId]);

  if (status.kind === 'loading') {
    return <main className="party-page"><p role="status">{t('common.loading')}</p></main>;
  }
  if (status.kind === 'missing' || status.kind === 'error') {
    return (
      <main className="party-page">
        <p className="inline-error" role="alert">
          {status.kind === 'missing' ? t('party.notFound') : t('party.loadError')}
        </p>
        <Link to="/parties">{t('party.back')}</Link>
      </main>
    );
  }

  const party = status.party;

  return (
    <main className="party-page" data-testid="party-workspace">
      <nav className="party-breadcrumb">
        <Link to="/parties">{t('party.back')}</Link>
      </nav>

      <header className="party-page-header">
        <div>
          <h1 data-testid="party-title">{party.title}</h1>
          <p className="party-header-meta">
            <span
              className={`party-badge party-badge--${party.status}`}
              data-testid="party-status" data-status={party.status}
            >
              {t(partyStatusLabelKey(party.status))}
            </span>
            {party.eventStartsAt && <> · {formatDate(party.eventStartsAt)}</>}
          </p>
        </div>
        <PartyLifecycleButton
          party={party}
          onPartyUpdated={(next) => setStatus({ kind: 'ready', party: next })}
        />
      </header>

      {/* A tablist, not a row of links: arrow keys and roving focus are what
          make this usable without a mouse. */}
      <div className="party-tabs" role="tablist" aria-label={t('party.title')}>
        {(['overview', 'before', 'live', 'after', 'photos'] as const).map((id) => (
          <button
            key={id} type="button" role="tab" id={`party-tab-${id}`}
            aria-selected={tab === id} aria-controls={`party-panel-${id}`}
            tabIndex={tab === id ? 0 : -1}
            className={`party-tab${tab === id ? ' is-active' : ''}`}
            data-testid={`party-tab-${id}`}
            onClick={() => setTab(id)}
          >
            {t(`party.tab.${id}` as 'party.tab.overview')}
          </button>
        ))}
      </div>

      <div
        role="tabpanel" id={`party-panel-${tab}`} aria-labelledby={`party-tab-${tab}`}
        className="party-panel"
      >
        {tab === 'overview' && (
          <PartyOverview
            party={party}
            albumParty={albumParty}
            onPartyUpdated={(next) => setStatus({ kind: 'ready', party: next })}
            onAlbumPartyUpdated={setAlbumParty}
          />
        )}
        {tab === 'before' && (
          <PartyContentTab
            partyId={party.id}
            slots={contentSlots}
            onSlotSaved={onSlotSaved}
            kinds={['invitation', 'location', 'dress-code', 'menu', 'info']}
            phases={['before', 'live']}
          />
        )}
        {tab === 'after' && (
          <div className="party-overview">
            <PartyContentTab
              partyId={party.id}
              slots={contentSlots}
              onSlotSaved={onSlotSaved}
              kinds={['thank-you']}
              phases={['after']}
            />
            {/* The memories' own window, which may OUTLIVE guest access — that
                is the whole point of it, and why it is configured here where it
                means something rather than beside the guest switch. */}
            <section className="party-card" data-testid="party-library-window">
              <h3>{t('party.tab.after')}</h3>
              <label className="party-field">
                <span>{t('party.after.libraryLabel')}</span>
                <input
                  type="datetime-local" value={libraryDraft} disabled={librarySaving}
                  aria-label={t('party.after.libraryLabel')}
                  onChange={(e) => setLibraryDraft(e.target.value)}
                />
              </label>
              <p className="muted">{t('party.after.libraryHelp')}</p>
              <button
                type="button" className="row-action-primary" data-testid="party-library-save"
                disabled={librarySaving} onClick={() => void saveLibraryWindow()}
              >
                {t('party.overview.save')}
              </button>
            </section>
            {/* The informational slots the host may also want to keep visible
                afterwards — the same cards, scoped to the After surface. */}
            <PartyContentTab
              partyId={party.id}
              slots={contentSlots}
              onSlotSaved={onSlotSaved}
              kinds={['location', 'info']}
              phases={['after']}
            />
          </div>
        )}
        {tab === 'live' && (
          <PartyLiveTab
            albumId={mainAlbumId}
            albumParty={albumParty}
            onAlbumPartyUpdated={setAlbumParty}
          />
        )}
        {tab === 'photos' && (
          <section className="party-card" data-testid="party-photos">
            <h3>{t('party.photos.heading')}</h3>
            {main ? (
              <>
                {/* No second album browser: the Media Library and the album
                    page are where photographs are looked at, and this points
                    at them rather than reproducing them. */}
                <p className="muted">{t('party.photos.help')}</p>
                <p className="party-album-name">{main.albumName}</p>
                <p className="party-card-actions">
                  <Link to={`/albums/${main.albumId}`}>{t('party.album.open')}</Link>
                </p>
              </>
            ) : (
              <PartyAlbumSection
                party={party}
                onPartyUpdated={(next) => setStatus({ kind: 'ready', party: next })}
              />
            )}
          </section>
        )}
      </div>
    </main>
  );
}

// One card per kind, in the server's order, filtered to the kinds this surface
// is about. It renders the slots it is given rather than inventing any: an
// untouched slot arrives at version 0 with the product's default visibility.
function PartyContentTab({
  partyId, slots, kinds, phases, onSlotSaved,
}: {
  partyId: string;
  slots: readonly PartyGuestContentSlot[];
  kinds: readonly PartyGuestContentKind[];
  phases: readonly ('before' | 'live' | 'after')[];
  onSlotSaved(next: PartyGuestContentSlot): void;
}) {
  return (
    <div className="party-overview">
      {slots
        .filter((slot) => kinds.includes(slot.kind))
        .map((slot) => (
          <PartyContentCard
            key={slot.kind}
            slot={slot}
            partyId={partyId}
            phases={phases}
            onSaved={onSlotSaved}
          />
        ))}
    </div>
  );
}

// The ONE lifecycle control, and only when there is a real move to make.
// `draft` has none — a party is published by opening it to guests — and `ended`
// has none, because there is no re-open transition and a button that answered
// 400 would be worse than no button.
function PartyLifecycleButton({
  party, onPartyUpdated,
}: {
  party: Party;
  onPartyUpdated(next: Party): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const primary = partyPrimaryAction(party.status);
  if (!primary) return null;

  async function run(action: PartyLifecycleAction) {
    if (action === 'end-live' && !window.confirm(t('party.action.confirmEnd'))) return;
    setBusy(true); setError(null);
    try {
      onPartyUpdated(await transitionParty(party.id, action, party.version));
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      // A refusal carries the CURRENT party, so the page adopts the server's
      // state rather than keeping the one it acted on. That is what makes a
      // version conflict a refresh instead of an overwrite.
      const body = (err as ApiError).body as { party?: Party } | undefined;
      if (body?.party) onPartyUpdated(body.party);
      setError(t('party.action.failed'));
    } finally { setBusy(false); }
  }

  return (
    <div className="party-primary-action">
      <button
        type="button" className="row-action-primary" data-testid="party-lifecycle-action"
        disabled={busy} onClick={() => void run(primary.action)}
      >
        {t(primary.labelKey)}
      </button>
      {error && <p className="inline-error" role="alert">{error}</p>}
    </div>
  );
}

function PartyOverview({
  party, albumParty, onPartyUpdated, onAlbumPartyUpdated,
}: {
  party: Party;
  albumParty: AlbumPartyStatus | null;
  onPartyUpdated(next: Party): void;
  onAlbumPartyUpdated(next: AlbumPartyStatus): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const main = mainMediaSource(party);
  const [draft, setDraft] = useState({
    title: party.title,
    description: party.description ?? '',
    eventStartsAt: toLocalInput(party.eventStartsAt),
    guestAccessExpiresAt: toLocalInput(party.guestAccessExpiresAt),
  });
  const [busy, setBusy] = useState(false);
  const [state, setState] = useState<'idle' | 'saved' | 'conflict' | 'failed'>('idle');

  // Re-seeded whenever the SERVER's version moves — including after a conflict,
  // which is how the form adopts what actually happened instead of insisting on
  // what the host typed against a stale read.
  useEffect(() => {
    setDraft({
      title: party.title,
      description: party.description ?? '',
      eventStartsAt: toLocalInput(party.eventStartsAt),
      guestAccessExpiresAt: toLocalInput(party.guestAccessExpiresAt),
    });
  }, [party.version, party.title, party.description, party.eventStartsAt, party.guestAccessExpiresAt]);

  const dirty =
    draft.title.trim() !== party.title
    || draft.description.trim() !== (party.description ?? '')
    || draft.eventStartsAt !== toLocalInput(party.eventStartsAt)
    || draft.guestAccessExpiresAt !== toLocalInput(party.guestAccessExpiresAt);

  async function save() {
    if (draft.title.trim() === '') return;
    setBusy(true); setState('idle');
    try {
      onPartyUpdated(await updateParty(party.id, {
        title: draft.title.trim(),
        description: draft.description.trim() || null,
        eventStartsAt: fromLocalInput(draft.eventStartsAt),
        guestAccessExpiresAt: fromLocalInput(draft.guestAccessExpiresAt),
        version: party.version,
      }));
      setState('saved');
    } catch (err) {
      if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
      const body = (err as ApiError).body as { party?: Party } | undefined;
      if (err instanceof ApiError && err.status === 409 && body?.party) {
        onPartyUpdated(body.party);
        setState('conflict');
      } else {
        setState('failed');
      }
    } finally { setBusy(false); }
  }

  return (
    <div className="party-overview">
      <section className="party-card" data-testid="party-details">
        <h3>{t('party.overview.details')}</h3>
        <label className="party-field">
          <span>{t('party.overview.titleLabel')}</span>
          {/* Renaming a party renames the PARTY. The album keeps its own name:
              they were only ever the same string because one was made from the
              other, and there is deliberately no sync in either direction. */}
          <input
            value={draft.title} disabled={busy}
            aria-label={t('party.overview.titleLabel')}
            onChange={(e) => setDraft((d) => ({ ...d, title: e.target.value }))}
          />
        </label>
        <label className="party-field">
          <span>{t('party.overview.dateLabel')}</span>
          <input
            type="datetime-local" value={draft.eventStartsAt} disabled={busy}
            aria-label={t('party.overview.dateLabel')}
            onChange={(e) => setDraft((d) => ({ ...d, eventStartsAt: e.target.value }))}
          />
        </label>
        <label className="party-field">
          <span>{t('party.overview.descriptionLabel')}</span>
          <textarea
            value={draft.description} rows={2} disabled={busy}
            aria-label={t('party.overview.descriptionLabel')}
            onChange={(e) => setDraft((d) => ({ ...d, description: e.target.value }))}
          />
        </label>
        {/* The party's OWN guest window, independent of any one QR's expiry: it
            closes every capability at once, and it is the public seam that
            enforces it rather than anything on this page. It is edited here
            because it is the party's data and shares its version — one form,
            one save, one concurrency check. */}
        <label className="party-field">
          <span>{t('party.guest.expiresLabel')}</span>
          <input
            type="datetime-local" value={draft.guestAccessExpiresAt} disabled={busy}
            aria-label={t('party.guest.expiresLabel')}
            onChange={(e) => setDraft((d) => ({ ...d, guestAccessExpiresAt: e.target.value }))}
          />
        </label>
        <p className="muted">{t('party.guest.expiresHelp')}</p>
        <button
          type="button" className="row-action-primary" data-testid="party-details-save"
          disabled={busy || !dirty || draft.title.trim() === ''} onClick={() => void save()}
        >
          {t('party.overview.save')}
        </button>
        {state === 'saved' && <p className="muted" role="status">{t('party.overview.saved')}</p>}
        {state === 'conflict' && (
          <p className="inline-error" role="alert" data-testid="party-conflict">
            {t('party.overview.conflict')}
          </p>
        )}
        {state === 'failed' && (
          <p className="inline-error" role="alert">{t('party.overview.saveFailed')}</p>
        )}
      </section>

      <PartyAlbumSection party={party} onPartyUpdated={onPartyUpdated} />

      {main ? (
        <PartyGuestAccessSection
          party={party}
          albumId={main.albumId}
          albumParty={albumParty}
          onAlbumPartyUpdated={onAlbumPartyUpdated}
        />
      ) : (
        <section className="party-card party-card--empty" data-testid="party-guest-needs-album">
          <h3>{t('party.guest.heading')}</h3>
          <p className="muted">{t('party.guest.needsAlbum')}</p>
        </section>
      )}

      {/* A DESCRIPTION of where the evening is, never a state editor: no step
          here can be clicked to move the party. */}
      <section className="party-card" data-testid="party-timeline">
        <h3>{t('party.overview.timeline')}</h3>
        <ol className="party-timeline">
          {PARTY_TIMELINE.map((step) => {
            const stepState = timelineStepState(step, party.status);
            return (
              <li
                key={step}
                className={`party-timeline-step is-${stepState}`}
                data-step={step} data-state={stepState}
                aria-current={stepState === 'current' ? 'step' : undefined}
              >
                <span className="party-timeline-label">{t(partyStatusLabelKey(step))}</span>
                {stepState !== 'upcoming' && (
                  <span className="visually-hidden">
                    {stepState === 'current'
                      ? t('party.overview.stepCurrent') : t('party.overview.stepDone')}
                  </span>
                )}
              </li>
            );
          })}
        </ol>
      </section>
    </div>
  );
}

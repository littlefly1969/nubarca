import { useCallback, useEffect, useRef, useState } from 'react';
import { Link, useParams, useSearchParams } from 'react-router';
import {
  ApiError,
  GUEST_CONSOLE_PARAMS,
  LEGACY_GUEST_SEARCH_PARAM,
  getParty,
  type GuestDirectoryState,
  type Party,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { PartyGuestListTab } from '../party/PartyGuestListTab';
import { partyStatusLabelKey } from '../party/partyModel';
import { PartyActivitiesSection } from '../party/workspace/PartyActivitiesSection';
import { PartyExperienceSection } from '../party/workspace/PartyExperienceSection';
import { PartyLiveSection } from '../party/workspace/PartyLiveSection';
import { PartyPhotosSection } from '../party/workspace/PartyPhotosSection';
import { PartyScreensSection } from '../party/workspace/PartyScreensSection';
import { PartySettingsSection } from '../party/workspace/PartySettingsSection';
import { PartySummarySection } from '../party/workspace/PartySummarySection';
import {
  defaultWorkspaceSection,
  isWorkspaceSection,
  sectionLabelKey,
  workspaceSections,
  type WorkspaceFacts,
  type WorkspaceSection,
} from '../party/workspace/partyWorkspaceModel';
import { usePartyFacts } from '../party/workspace/usePartyFacts';
import { Notice, PanelSkeleton } from '../party/workspace/ui';
import '../party/Party.css';
import '../party/workspace/PartyWorkspace.css';

// THE host's surface for one party.
//
// Seven sections, and the same seven from the first draft to the last
// photograph — plus Live, which exists only while the party is. A host learns
// one map and keeps it: what changes with the lifecycle is which section they
// land on, what each one leads with, and which steps are still open, never the
// shape of the product.
//
//   Riepilogo          what to do now
//   Live               the evening, while it is happening
//   Esperienza         what the guests will see
//   Ospiti             the guest list, the invitations, the door
//   Foto               where the photographs live and who may add to them
//   Attività           the game and the greetings
//   Schermi e stampa   the television, the paired display, the printer
//   Impostazioni       the party's own facts, its windows, and removing it
//
// Everything below the party's root is reached through its MAIN ALBUM, using
// the functions that already work. This page resolves that album once and hands
// the id down; nothing here mints a token, moderates a photograph or configures
// a printer of its own.

/** The old `?tab=` values, so a bookmark from before this release still lands. */
const LEGACY_TABS: Record<string, WorkspaceSection> = {
  overview: 'summary',
  before: 'experience',
  after: 'experience',
  guests: 'guests',
  photos: 'photos',
  // The old "Live" tab was the evening's CONFIGURATION — contributions, the
  // game, printing — which now lives in the sections that own each decision.
  // It lands on Activities; a party that really is live opens its console by
  // default anyway.
  live: 'activities',
};

/** Sections whose content states what is waiting in the moderation queues. */
const SHOWS_MODERATION: readonly WorkspaceSection[] = ['summary', 'live', 'photos', 'activities'];

type Status =
  | { kind: 'loading' }
  | { kind: 'ready'; party: Party }
  | { kind: 'missing' }
  | { kind: 'error' };

export function PartyWorkspacePage() {
  const { partyId } = useParams<{ partyId: string }>();
  const { t, formatDate } = useI18n();
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [searchParams, setSearchParams] = useSearchParams();
  const abortRef = useRef<AbortController | null>(null);

  const party = status.kind === 'ready' ? status.party : null;
  const partyStatus = party?.status ?? 'draft';
  const available = workspaceSections(partyStatus);

  // The guest search is never in a URL any more, but a link made before it
  // moved out can still carry one — and the guest console can only strip what
  // arrives while IT is open. Dropping it here covers every other way in (a
  // bookmark to another section, a pasted link), by REPLACING the entry so it
  // is not one Back away either. It is removed, never read.
  useEffect(() => {
    if (!searchParams.has(LEGACY_GUEST_SEARCH_PARAM)) return;
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      next.delete(LEGACY_GUEST_SEARCH_PARAM);
      return next;
    }, { replace: true });
  }, [searchParams, setSearchParams]);

  // What the URL asks for, what a pre-release bookmark asks for, and what the
  // party's own phase would open. Anything the party does not offer right now
  // falls back rather than rendering an empty panel.
  const asked = searchParams.get('section') ?? LEGACY_TABS[searchParams.get('tab') ?? ''] ?? null;
  const wanted = isWorkspaceSection(asked) ? asked : null;
  const section: WorkspaceSection = wanted && available.includes(wanted)
    ? wanted
    : defaultWorkspaceSection(partyStatus);

  const facts = usePartyFacts(
    party,
    { wantsModeration: SHOWS_MODERATION.includes(section) },
    invalidateAuth,
  );

  const setSection = useCallback((next: WorkspaceSection) => {
    setSearchParams((current) => {
      const params = new URLSearchParams(current);
      params.set('section', next);
      params.delete('tab');
      if (next !== 'guests') {
        // The guest console's own state belongs to the guest console. Its
        // search is not here at all — it never enters a URL — but leaving the
        // section is another chance to drop what a stale link carried.
        params.delete(LEGACY_GUEST_SEARCH_PARAM);
        params.delete(GUEST_CONSOLE_PARAMS.state);
        params.delete(GUEST_CONSOLE_PARAMS.group);
      }
      return params;
    }, { replace: true });
  }, [setSearchParams]);

  /** The door, already filtered: the live console's shortcuts into the list. */
  const openGuests = useCallback((filter: GuestDirectoryState | null) => {
    setSearchParams((current) => {
      const params = new URLSearchParams(current);
      params.set('section', 'guests');
      params.delete('tab');
      params.delete(GUEST_CONSOLE_PARAMS.group);
      if (filter === null || filter === 'all') params.delete(GUEST_CONSOLE_PARAMS.state);
      else params.set(GUEST_CONSOLE_PARAMS.state, filter);
      return params;
    }, { replace: true });
  }, [setSearchParams]);

  const load = useCallback((signal: AbortSignal) => {
    if (!partyId) return;
    getParty(partyId, signal)
      .then((next) => setStatus({ kind: 'ready', party: next }))
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

  const onPartyUpdated = useCallback((next: Party) => {
    setStatus({ kind: 'ready', party: next });
  }, []);

  if (status.kind === 'loading') {
    return (
      <main className="pw" data-testid="party-workspace">
        {/* Content-shaped, so what arrives does not move the page under a
            thumb that is already reaching for it. */}
        <div className="pw-head">
          <div className="pw-skeleton pw-skeleton--title" aria-hidden />
        </div>
        <PanelSkeleton rows={3} />
        <p role="status" className="visually-hidden">{t('common.loading')}</p>
      </main>
    );
  }

  if (status.kind === 'missing' || status.kind === 'error') {
    return (
      <main className="pw" data-testid="party-workspace">
        <div className="pw-head">
          <Link to="/parties" className="pw-back">← {t('party.back')}</Link>
        </div>
        <Notice
          tone="error"
          testId="party-load-error"
          title={t(status.kind === 'missing' ? 'party.notFound' : 'party.loadError')}
        >
          <p>{t(status.kind === 'missing' ? 'party.notFoundBody' : 'party.loadErrorBody')}</p>
        </Notice>
      </main>
    );
  }

  const current = status.party;
  const workspaceFacts: WorkspaceFacts = {
    party: current,
    albumParty: facts.albumParty,
    slots: facts.slots,
    guests: facts.guests,
    moderation: facts.moderation,
  };

  return (
    <main className="pw" data-testid="party-workspace">
      <header className="pw-head">
        <Link to="/parties" className="pw-back">← {t('party.back')}</Link>
        <div className="pw-head-main">
          <div className="pw-identity">
            <h1 className="pw-title" data-testid="party-title">{current.title}</h1>
            <p className="pw-head-meta">
              {/* State is never carried by colour alone: the badge says the
                  word, and it is a product label rather than a raw `draft`. */}
              <span
                className={`pw-badge pw-badge--${current.status}`}
                data-testid="party-status" data-status={current.status}
              >
                {t(partyStatusLabelKey(current.status))}
              </span>
              {current.eventStartsAt && (
                <>
                  <span className="pw-dot" aria-hidden>·</span>
                  <span>{formatDate(current.eventStartsAt)}</span>
                </>
              )}
            </p>
          </div>
        </div>
      </header>

      <div className="pw-layout">
        {/* A tablist, not a row of links: arrow keys and roving focus are what
            make this usable without a mouse, and the same markup becomes a
            rail on a wide screen rather than a second component. */}
        <nav className="pw-nav" role="tablist" aria-label={t('party.section.nav')}>
          {available.map((id) => (
            <button
              key={id} type="button" role="tab"
              id={`party-tab-${id}`}
              aria-selected={section === id}
              aria-controls={`party-panel-${id}`}
              tabIndex={section === id ? 0 : -1}
              className="pw-nav-item"
              data-live={id === 'live' ? 'true' : undefined}
              data-testid={`party-tab-${id}`}
              onClick={() => setSection(id)}
            >
              {id === 'live' && <span className="pw-nav-dot" aria-hidden />}
              <span>{t(sectionLabelKey(id))}</span>
              <SectionCount section={id} facts={workspaceFacts} />
            </button>
          ))}
        </nav>

        <div
          role="tabpanel"
          id={`party-panel-${section}`}
          aria-labelledby={`party-tab-${section}`}
          className="pw-panels"
        >
          {section === 'summary' && (
            <PartySummarySection
              facts={workspaceFacts}
              onNavigate={setSection}
              onPartyUpdated={onPartyUpdated}
              onAlbumPartyUpdated={facts.setAlbumParty}
            />
          )}

          {section === 'live' && (
            <PartyLiveSection
              facts={workspaceFacts}
              onNavigate={setSection}
              onOpenGuests={openGuests}
              onPartyUpdated={onPartyUpdated}
              onRefresh={facts.refresh}
            />
          )}

          {section === 'experience' && (
            <PartyExperienceSection
              party={current}
              albumParty={facts.albumParty}
              slots={facts.slots}
              onPartyUpdated={onPartyUpdated}
              onSlotSaved={facts.setSlot}
            />
          )}

          {section === 'guests' && (
            <PartyGuestListTab party={current} onPartyUpdated={onPartyUpdated} />
          )}

          {section === 'photos' && (
            <PartyPhotosSection
              party={current}
              albumParty={facts.albumParty}
              moderation={facts.moderation}
              onPartyUpdated={onPartyUpdated}
              onAlbumPartyUpdated={facts.setAlbumParty}
              onNavigate={setSection}
            />
          )}

          {section === 'activities' && (
            <PartyActivitiesSection
              party={current}
              albumParty={facts.albumParty}
              moderation={facts.moderation}
              onAlbumPartyUpdated={facts.setAlbumParty}
            />
          )}

          {section === 'screens' && (
            <PartyScreensSection
              party={current}
              albumParty={facts.albumParty}
              onAlbumPartyUpdated={facts.setAlbumParty}
            />
          )}

          {section === 'settings' && (
            <PartySettingsSection
              party={current}
              albumParty={facts.albumParty}
              onPartyUpdated={onPartyUpdated}
              onAlbumPartyUpdated={facts.setAlbumParty}
            />
          )}
        </div>
      </div>
    </main>
  );
}

/**
 * What is waiting in a section, beside its name — and nothing when nothing is.
 *
 * A badge that always shows a number teaches a host to stop reading it. These
 * appear only when there is something to act on, which is what makes them worth
 * a glance during an evening.
 */
function SectionCount({
  section, facts,
}: {
  section: WorkspaceSection;
  facts: WorkspaceFacts;
}) {
  const { t } = useI18n();
  const waiting = section === 'photos'
    ? facts.moderation?.uploads ?? 0
    : section === 'activities'
      ? facts.moderation?.messages ?? 0
      : 0;
  if (waiting <= 0) return null;
  return (
    <>
      <span className="pw-nav-count" aria-hidden>{waiting}</span>
      <span className="visually-hidden">{t('party.photos.pending', { count: waiting })}</span>
    </>
  );
}

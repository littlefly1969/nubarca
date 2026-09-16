import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { useLocation, useNavigate, useSearchParams } from 'react-router';
import {
  ApiError,
  GUEST_CONSOLE_PARAMS,
  PARTY_ATTENDANCE_LIMITS,
  checkInPartyGuest,
  codePoints,
  createPartyAttendanceGuest,
  deletePartyAttendanceGuest,
  deletePartyInvitationGroup,
  getPartyGuestDirectory,
  getPartyInvitationGroup,
  getPartyRsvpQuestions,
  guestDirectoryStatesFor,
  isAttendancePhase,
  isGuestDirectoryState,
  normalizeText,
  remindPartyInvitation,
  rotatePartyInvitationLink,
  sendPartyInvitation,
  undoPartyGuestCheckIn,
  unexpectedArrivals,
  updatePartyAttendanceGuest,
  type GuestDirectoryGroupItem,
  type GuestDirectoryOtherItem,
  type GuestDirectoryPerson,
  type GuestDirectoryState,
  type InvitationPrimaryAction,
  type InvitationShareChannel,
  type Party,
  type PartyInvitationGroup,
  type PartyInvitationGroupDetail,
  type PartyRsvpQuestion,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useAppScrollViewport } from '../components/appScroll';
import { Modal } from '../components/Overlay';
import { useI18n, type MessageKey } from '../i18n';
import { GuestActionSheet, type GuestAction } from './GuestActionSheet';
import { GuestGroupCard, GuestOtherCard } from './GuestGroupCard';
import { GuestGroupDetail, type DetailTarget, type GuestDetailActions } from './GuestGroupDetail';
import { GuestGroupEditor } from './GuestGroupEditor';
import { GuestSharedLink } from './GuestSharedLink';
import { PartyRsvpQuestionsCard } from './PartyRsvpQuestionsCard';
import { newClientRequestId } from './clientRequestId';
import { shareInvitation, type ShareOutcome } from './guestConsoleActions';
import { useGuestDirectory } from './useGuestDirectory';
import { useWideLayout } from './useWideLayout';
import './PartyGuestConsole.css';

// "OSPITI" — the host's console, not a list of people.
//
// The same surface serves a party of ten and one of a thousand, because it
// never holds the whole guest list: the SERVER searches, filters and orders it,
// and the console shows the page it asked for. What the host sees first is the
// few numbers that matter, one search field and the filters of the phase they
// are in; each group is a card that opens on demand.
//
// Before the party it is about the invitation: one primary action shares the
// group's personal link — WhatsApp, email or a copied link, all the same link —
// and the rest is in a menu a thumb can reach. From the moment the party is
// live the same cards become the door: every person one tap from being
// recorded as arrived, with the search finding them in a couple of letters.
//
// The search, the filter and the open group live in the URL, so Back closes a
// group exactly where the host left the list, and a reload returns to it.

type Busy = Record<string, true>;

type Notice = { tone: 'ok' | 'error'; text: string } | null;

type Menu =
  | { kind: 'group'; item: GuestDirectoryGroupItem }
  | { kind: 'other'; item: GuestDirectoryOtherItem }
  | null;

type Confirm =
  | { kind: 'rotate'; target: DetailTarget }
  | { kind: 'remove'; target: DetailTarget }
  | { kind: 'removeOther'; item: GuestDirectoryOtherItem }
  | null;

const ERROR_KEYS: Record<string, MessageKey> = {
  version_conflict: 'party.console.error.version_conflict',
  party_version_conflict: 'party.console.error.party_version_conflict',
  invitations_closed: 'party.console.error.invitations_closed',
  reminder_not_allowed: 'party.console.error.reminder_not_allowed',
  mail_unavailable: 'party.console.mailUnavailable',
  link_unavailable: 'party.console.shareUnavailable',
  additional_guests_in_use: 'party.console.error.additional_guests_in_use',
  attendance_not_open: 'party.console.error.attendance_not_open',
  invalid_name: 'party.console.error.invalid_name',
};

const SEARCH_DEBOUNCE_MS = 250;

export function PartyGuestListTab({
  party, onPartyUpdated,
}: {
  party: Party;
  onPartyUpdated(next: Party): void;
}) {
  const { t } = useI18n();
  const { invalidateAuth } = useAuth();
  const [searchParams, setSearchParams] = useSearchParams();
  const location = useLocation();
  const navigate = useNavigate();
  const wide = useWideLayout();
  const viewport = useAppScrollViewport();

  const urlSearch = searchParams.get(GUEST_CONSOLE_PARAMS.search) ?? '';
  const stateParam = searchParams.get(GUEST_CONSOLE_PARAMS.state);
  const state: GuestDirectoryState = isGuestDirectoryState(stateParam) ? stateParam : 'all';
  const openGroupId = searchParams.get(GUEST_CONSOLE_PARAMS.group);

  const [typed, setTyped] = useState(urlSearch);
  const [busy, setBusy] = useState<Busy>({});
  const [notice, setNotice] = useState<Notice>(null);
  const [menu, setMenu] = useState<Menu>(null);
  const [confirm, setConfirm] = useState<Confirm>(null);
  const [share, setShare] = useState<ShareOutcome | null>(null);
  const [editing, setEditing] = useState<PartyInvitationGroup | 'new' | null>(null);
  const [adding, setAdding] = useState(false);
  const [renaming, setRenaming] = useState<GuestDirectoryOtherItem | null>(null);
  const [detailRefresh, setDetailRefresh] = useState(0);
  const [questions, setQuestions] = useState<PartyRsvpQuestion[] | null>(null);
  const sentinel = useRef<HTMLDivElement>(null);

  const directory = useGuestDirectory(party.id, { q: urlSearch, state }, invalidateAuth);
  const live = isAttendancePhase(party.status);

  // --- The URL is the console's state ------------------------------------------

  const setParam = useCallback((key: string, value: string | null, options?: { push?: boolean }) => {
    setSearchParams((current) => {
      const next = new URLSearchParams(current);
      if (value === null || value === '') next.delete(key);
      else next.set(key, value);
      return next;
    }, options?.push ? { state: { guestDetail: true } } : { replace: true });
  }, [setSearchParams]);

  // Typing moves the field at once and the list a moment later; a search that
  // arrives from elsewhere (Back, "azzera") is adopted.
  useEffect(() => {
    setTyped((current) => (current.trim() === urlSearch ? current : urlSearch));
  }, [urlSearch]);
  useEffect(() => {
    const trimmed = typed.trim();
    if (trimmed === urlSearch) return;
    const timer = setTimeout(() => setParam(GUEST_CONSOLE_PARAMS.search, trimmed), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [typed, urlSearch, setParam]);

  const hrefFor = useCallback((groupId: string) => {
    const next = new URLSearchParams(searchParams);
    next.set(GUEST_CONSOLE_PARAMS.group, groupId);
    return `?${next.toString()}`;
  }, [searchParams]);

  const closeDetail = useCallback(() => {
    // Opening a group pushed an entry, so Back is the way out — which is also
    // what a phone's own back gesture does. A link opened from outside has no
    // entry to go back to.
    if ((location.state as { guestDetail?: boolean } | null)?.guestDetail) navigate(-1);
    else setParam(GUEST_CONSOLE_PARAMS.group, null);
  }, [location.state, navigate, setParam]);

  const openDetail = useCallback((groupId: string) => {
    setParam(GUEST_CONSOLE_PARAMS.group, groupId, { push: true });
  }, [setParam]);

  // --- Reading back what a write changed ---------------------------------------

  const mark = useCallback((key: string, on: boolean) => {
    setBusy((current) => {
      if (!on) {
        const next = { ...current };
        delete next[key];
        return next;
      }
      return { ...current, [key]: true };
    });
  }, []);

  // Bound to the directory's own stable writers, not to the view object it
  // returns: this is the open group's `onLoaded`, and an identity that changed
  // every render would re-run its effect — a group that fetched itself forever.
  const { patchGroup, setSummary, reload, loadMore, hasMore } = directory;
  const adoptDetail = useCallback((detail: PartyInvitationGroupDetail) => {
    patchGroup(detail.item);
    setSummary(detail.summary);
  }, [patchGroup, setSummary]);

  const refreshGroup = useCallback(async (groupId: string) => {
    if (openGroupId === groupId) {
      // The open detail re-reads and reports what it found.
      setDetailRefresh((n) => n + 1);
      return;
    }
    adoptDetail(await getPartyInvitationGroup(party.id, groupId));
  }, [openGroupId, adoptDetail, party.id]);

  const refreshSummary = useCallback(async () => {
    const page = await getPartyGuestDirectory(party.id, { take: 0 });
    setSummary(page.summary);
  }, [party.id, setSummary]);

  const refused = useCallback((err: unknown, fallback: MessageKey) => {
    if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
    const body = (err instanceof ApiError ? err.body : null) as { error?: string; party?: Party } | null;
    if (body?.party) onPartyUpdated(body.party);
    setNotice({ tone: 'error', text: t((body?.error && ERROR_KEYS[body.error]) || fallback) });
    // A refusal that describes a state is also a reason to re-read it.
    if (body?.error === 'version_conflict' || (err instanceof ApiError && err.status === 404)) {
      reload();
    }
  }, [invalidateAuth, onPartyUpdated, t, reload]);

  const run = useCallback(async (key: string, work: () => Promise<void>) => {
    mark(key, true);
    setNotice(null);
    try {
      await work();
    } catch (err) {
      refused(err, 'party.console.error.generic');
    } finally {
      mark(key, false);
    }
  }, [mark, refused]);

  // --- What the host can do -----------------------------------------------------

  const runShare = useCallback((target: DetailTarget, channel: InvitationShareChannel) =>
    run(target.groupId, async () => {
      const { result, outcome } = await shareInvitation(party.id, target.groupId, channel, party.version);
      if (result.party) onPartyUpdated(result.party);
      if (result.item) directory.patchGroup(result.item);
      setShare(outcome);
      if (openGroupId === target.groupId) setDetailRefresh((n) => n + 1);
      setNotice({
        tone: 'ok',
        text: channel === 'copy'
          ? t(outcome.copied ? 'party.console.share.copied' : 'party.console.share.copyFallback')
          : t(outcome.opened ? 'party.console.share.whatsappOpened' : 'party.console.share.whatsappBlocked'),
      });
    }), [run, party.id, party.version, onPartyUpdated, directory, openGroupId, t]);

  const runEmail = useCallback((target: DetailTarget, reminder: boolean) =>
    run(target.groupId, async () => {
      const clientRequestId = newClientRequestId();
      const result = reminder
        ? await remindPartyInvitation(party.id, target.groupId, clientRequestId)
        : await sendPartyInvitation(party.id, target.groupId, { clientRequestId, partyVersion: party.version });
      onPartyUpdated(result.party);
      await refreshGroup(target.groupId);
      const status = result.delivery.status;
      setNotice(status === 'sent'
        ? {
          tone: 'ok',
          text: reminder ? t('party.console.reminded') : t('party.console.sent', { label: target.label }),
        }
        : {
          tone: 'error',
          text: t(status === 'failed' ? 'party.console.sendFailed' : 'party.console.sendPending'),
        });
    }), [run, party.id, party.version, onPartyUpdated, refreshGroup, t]);

  const runRotate = useCallback((target: DetailTarget) =>
    run(target.groupId, async () => {
      await rotatePartyInvitationLink(party.id, target.groupId, target.version);
      setShare(null);
      await refreshGroup(target.groupId);
      setNotice({ tone: 'ok', text: t('party.console.rotated') });
    }), [run, party.id, refreshGroup, t]);

  const runRemove = useCallback((target: DetailTarget) =>
    run(target.groupId, async () => {
      await deletePartyInvitationGroup(party.id, target.groupId, target.version);
      directory.removeItem(`g:${target.groupId}`);
      if (openGroupId === target.groupId) closeDetail();
      await refreshSummary();
      setNotice({ tone: 'ok', text: t('party.console.removed', { label: target.label }) });
    }), [run, party.id, directory, openGroupId, closeDetail, refreshSummary, t]);

  const patchPerson = useCallback((
    groupId: string, guestId: string, arrival: { checkedInAt: string | null; checkInSource: 'owner' | 'invitation' | null },
  ) => {
    const item = directory.items.find(
      (candidate): candidate is GuestDirectoryGroupItem => candidate.kind === 'group' && candidate.groupId === groupId);
    if (!item) return;
    const people = item.people.map((person) => (person.guestId === guestId ? { ...person, ...arrival } : person));
    directory.patchGroup({
      ...item,
      people,
      counts: { ...item.counts, arrived: people.filter((person) => person.checkedInAt !== null).length },
    });
  }, [directory]);

  const runArrival = useCallback((target: DetailTarget, person: GuestDirectoryPerson, undo: boolean) =>
    run(`guest:${person.guestId}`, async () => {
      const change = undo
        ? await undoPartyGuestCheckIn(party.id, person.guestId)
        : await checkInPartyGuest(party.id, person.guestId);
      patchPerson(target.groupId, person.guestId, {
        checkedInAt: change.guest?.checkedInAt ?? null,
        checkInSource: change.guest?.checkInSource ?? null,
      });
      directory.patchAttendance(change.summary);
      if (openGroupId === target.groupId) setDetailRefresh((n) => n + 1);
      setNotice({
        tone: 'ok',
        text: t(undo ? 'party.console.undone' : 'party.console.checkedIn', { name: person.name }),
      });
    }), [run, party.id, patchPerson, directory, openGroupId, t]);

  const addPerson = useCallback((name: string, clientRequestId: string) =>
    run('add', async () => {
      const change = await createPartyAttendanceGuest(party.id, { name: name.trim(), clientRequestId });
      // An arrival becomes a card of the directory, which says what kind it is.
      if (change.otherGuest) directory.prependOther({ kind: 'other', ...change.otherGuest });
      directory.patchAttendance(change.summary);
      setAdding(false);
      setNotice({ tone: 'ok', text: t('party.console.added', { name: name.trim() }) });
    }), [run, party.id, directory, t]);

  const renameOther = useCallback((item: GuestDirectoryOtherItem, name: string) =>
    run(`other:${item.id}`, async () => {
      const change = await updatePartyAttendanceGuest(party.id, item.id, { name: name.trim(), version: item.version });
      if (change.otherGuest) directory.patchOther({ kind: 'other', ...change.otherGuest });
      setRenaming(null);
    }), [run, party.id, directory]);

  const removeOther = useCallback((item: GuestDirectoryOtherItem) =>
    run(`other:${item.id}`, async () => {
      const change = await deletePartyAttendanceGuest(party.id, item.id);
      directory.removeItem(`o:${item.id}`);
      directory.patchAttendance(change.summary);
      setNotice({ tone: 'ok', text: t('party.console.other.removed', { name: item.name }) });
    }), [run, party.id, directory, t]);

  const onPrimary = useCallback((item: GuestDirectoryGroupItem, action: InvitationPrimaryAction) => {
    const target: DetailTarget = { groupId: item.groupId, label: item.label, version: item.version };
    if (action === 'email') void runEmail(target, false);
    else void runShare(target, action);
  }, [runEmail, runShare]);

  const detailActions: GuestDetailActions = useMemo(() => ({
    share: (target, channel) => void runShare(target, channel),
    email: (target) => void runEmail(target, false),
    remind: (target) => void runEmail(target, true),
    rotate: (target) => setConfirm({ kind: 'rotate', target }),
    remove: (target) => setConfirm({ kind: 'remove', target }),
    edit: (detail) => setEditing(detail.group),
    checkIn: (target, person) => void runArrival(target, person, false),
    undo: (target, person) => void runArrival(target, person, true),
  }), [runShare, runEmail, runArrival]);

  // --- Paging as the host scrolls ------------------------------------------------

  useEffect(() => {
    const element = sentinel.current;
    if (!element || !hasMore || directory.status !== 'ready') return;
    // Rooted in the shell's scroll viewport: a document-rooted observer inside
    // `.app-main` would lose its preload margin to that container's clip.
    const observer = new IntersectionObserver((entries) => {
      if (entries.some((entry) => entry.isIntersecting)) loadMore();
    }, { root: viewport?.current ?? null, rootMargin: '600px 0px' });
    observer.observe(element);
    return () => observer.disconnect();
  }, [hasMore, directory.status, loadMore, viewport]);

  // The questions are read only when the host opens them.
  const openQuestions = useCallback(() => {
    if (questions !== null) return;
    getPartyRsvpQuestions(party.id)
      .then((answer) => setQuestions(answer.questions))
      .catch((err: unknown) => refused(err, 'party.console.error.generic'));
  }, [questions, party.id, refused]);

  // --- What is on the screen ------------------------------------------------------

  const summary = directory.summary;
  const groups = summary?.groups ?? 0;
  const hasGuestList = groups > 0;
  const filters = guestDirectoryStatesFor(party.status, hasGuestList);
  const searching = urlSearch !== '' || state !== 'all';
  const anything = directory.items.length > 0 || searching;
  const openItem = directory.items.find(
    (item): item is GuestDirectoryGroupItem => item.kind === 'group' && item.groupId === openGroupId);
  const detailBusy = openGroupId !== undefined && openGroupId !== null && Boolean(busy[openGroupId]);

  const detail = openGroupId && (
    <GuestGroupDetail
      partyId={party.id} groupId={openGroupId} refreshKey={detailRefresh} busy={detailBusy}
      share={share} actions={detailActions} onLoaded={adoptDetail} onUnauthorized={invalidateAuth}
    />
  );

  return (
    <div className="guest-console" data-testid="party-guests" data-layout={wide ? 'wide' : 'narrow'}>
      <div className="guest-console-head">
        <h2>{t('party.console.heading')}</h2>
        <button
          type="button" className="row-action" data-testid="guest-refresh"
          onClick={() => { directory.reload(); setNotice(null); }}
        >
          {t('party.console.refresh')}
        </button>
      </div>

      {summary && (hasGuestList || live) && (
        <dl className="guest-metrics" data-testid="guest-metrics" aria-label={t('party.console.metrics')}>
          {(live
            ? hasGuestList
              ? ([
                ['expected', summary.attendance.expectedPeople],
                ['arrived', summary.attendance.totalArrivals],
                ['missing', summary.attendance.expectedMissing],
                ['others', unexpectedArrivals(summary.attendance)],
              ] as const)
              : ([['recorded', summary.attendance.totalArrivals]] as const)
            : ([
              ['invited', summary.rsvp.invited],
              ['attending', summary.rsvp.attending],
              ['pending', summary.rsvp.missingResponses],
              ['declined', summary.rsvp.declined],
            ] as const)
          ).map(([key, value]) => (
            <div key={key} className="guest-metric" data-metric={key}>
              <dt>{t(`party.console.metric.${key}` as MessageKey)}</dt>
              <dd>{value}</dd>
            </div>
          ))}
        </dl>
      )}

      {live && !hasGuestList && (
        <p className="muted" data-testid="guest-recorded-note">{t('party.console.recordedNote')}</p>
      )}
      {live && party.status === 'live' && hasGuestList && (
        <p className="muted">{t('party.console.selfCheckInNote')}</p>
      )}
      {party.status === 'ended' && <p className="muted">{t('party.console.endedNote')}</p>}
      {!live && hasGuestList && party.status === 'draft' && (
        <p className="muted">{t('party.console.publishNote')}</p>
      )}
      {directory.meta && !directory.meta.mailAvailable && (
        <p className="muted" role="note" data-testid="guest-mail-unavailable">{t('party.console.mailUnavailable')}</p>
      )}
      {directory.meta && !directory.meta.shareAvailable && (
        <p className="muted" role="note" data-testid="guest-share-unavailable">{t('party.console.shareUnavailable')}</p>
      )}

      {anything && (
        <div className="guest-toolbar">
          <div className="guest-toolbar-row">
            <label className="party-field guest-search">
              <span className="visually-hidden">{t('party.console.searchLabel')}</span>
              <input
                type="search" value={typed} data-testid="guest-search"
                placeholder={t(live ? 'party.console.search.live' : 'party.console.search.before')}
                aria-label={t('party.console.searchLabel')}
                onChange={(e) => setTyped(e.target.value)}
              />
            </label>
            <button
              type="button" className="row-action-primary guest-add" data-testid="guest-add"
              onClick={() => (live ? setAdding(true) : setEditing('new'))}
            >
              {t(live ? 'party.console.addPerson' : 'party.console.addGroup')}
            </button>
          </div>
          {filters.length > 0 && (
            <div className="guest-filters" role="group" aria-label={t('party.console.filters')}>
              {filters.map((candidate) => (
                <button
                  key={candidate} type="button" className="row-action guest-filter"
                  aria-pressed={state === candidate} data-testid={`guest-filter-${candidate}`}
                  onClick={() => setParam(GUEST_CONSOLE_PARAMS.state, candidate === 'all' ? null : candidate)}
                >
                  {t(`party.console.filter.${candidate}` as MessageKey)}
                </button>
              ))}
            </div>
          )}
        </div>
      )}

      <div className="guest-notice" aria-live="polite">
        {notice && (
          <p
            className={notice.tone === 'error' ? 'inline-error' : 'muted'}
            role={notice.tone === 'error' ? 'alert' : 'status'} data-testid="guest-notice"
          >
            {notice.text}
          </p>
        )}
        {share && (!openGroupId || openGroupId !== share.groupId) && (
          <GuestSharedLink share={share} label={openItem?.label ?? ''} />
        )}
      </div>

      <div className="guest-body" data-wide={wide && Boolean(openGroupId)}>
        <section className="guest-list-pane" aria-label={t('party.console.list')}>
          {directory.status === 'error' ? (
            <div className="party-card">
              <p className="inline-error" role="alert">{t('party.console.loadError')}</p>
              <button type="button" className="row-action" data-testid="guest-retry" onClick={directory.reload}>
                {t('party.console.retry')}
              </button>
            </div>
          ) : directory.status === 'loading' ? (
            <ul className="guest-list" aria-busy data-testid="guest-loading">
              {[0, 1, 2].map((row) => <li key={row} className="guest-skeleton" aria-hidden />)}
              <li className="visually-hidden" role="status">{t('party.console.loading')}</li>
            </ul>
          ) : directory.items.length === 0 ? (
            <EmptyList
              live={live} searching={searching} query={urlSearch}
              onAddGroup={() => setEditing('new')} onAddPerson={() => setAdding(true)}
              onClear={() => setSearchParams((current) => {
                const next = new URLSearchParams(current);
                next.delete(GUEST_CONSOLE_PARAMS.search);
                next.delete(GUEST_CONSOLE_PARAMS.state);
                return next;
              }, { replace: true })}
            />
          ) : (
            <ul className="guest-list" data-testid="guest-list">
              {directory.items.map((item) => (item.kind === 'group' ? (
                <GuestGroupCard
                  key={`g:${item.groupId}`} item={item} live={live} selected={item.groupId === openGroupId}
                  busy={Boolean(busy[item.groupId])} detailHref={hrefFor(item.groupId)}
                  onPrimary={(action) => onPrimary(item, action)}
                  onMenu={() => setMenu({ kind: 'group', item })}
                  onCheckIn={(person) => void runArrival(
                    { groupId: item.groupId, label: item.label, version: item.version }, person, false)}
                  onUndo={(person) => void runArrival(
                    { groupId: item.groupId, label: item.label, version: item.version }, person, true)}
                />
              ) : (
                <GuestOtherCard
                  key={`o:${item.id}`} item={item} busy={Boolean(busy[`other:${item.id}`])}
                  onMenu={() => setMenu({ kind: 'other', item })}
                />
              )))}
            </ul>
          )}

          {directory.loadMoreFailed && (
            <p className="inline-error" role="alert" data-testid="guest-load-more-error">
              {t('party.console.loadMoreError')}
            </p>
          )}
          {directory.hasMore && (
            <div className="guest-more">
              <button
                type="button" className="row-action" data-testid="guest-load-more"
                disabled={directory.loadingMore} onClick={directory.loadMore}
              >
                {t(directory.loadingMore ? 'party.console.loadingMore' : 'party.console.loadMore')}
              </button>
            </div>
          )}
          <div ref={sentinel} aria-hidden className="guest-sentinel" />
        </section>

        {wide && openGroupId && (
          <aside className="guest-detail-pane" data-testid="guest-detail-pane">
            <div className="guest-detail-head">
              <h3>{openItem?.label ?? ''}</h3>
              <button
                type="button" className="row-action" data-testid="guest-detail-close" onClick={closeDetail}
              >
                {t('party.console.back')}
              </button>
            </div>
            {detail}
          </aside>
        )}
      </div>

      {!live && hasGuestList && (
        <details className="party-advanced guest-questions" data-testid="guest-questions" onToggle={openQuestions}>
          <summary>{t('party.console.questions')}</summary>
          {questions !== null && (
            <PartyRsvpQuestionsCard
              partyId={party.id} questions={questions} onChanged={setQuestions}
              onRefused={(err, fallback) => refused(err, fallback)}
            />
          )}
        </details>
      )}

      {!wide && openGroupId && (
        <Modal
          title={openItem?.label ?? t('party.console.heading')} onClose={closeDetail}
          testId="guest-detail-sheet" className="guest-sheet guest-sheet--detail"
          focusPanelOnOpen
        >
          {detail}
        </Modal>
      )}

      {menu?.kind === 'group' && (
        <GuestActionSheet
          title={t('party.console.action.sheetTitle', { label: menu.item.label })}
          testId="guest-menu-sheet" onClose={() => setMenu(null)}
          actions={groupActions(menu.item, {
            t,
            open: () => openDetail(menu.item.groupId),
            share: (channel) => void runShare(targetOf(menu.item), channel),
            email: () => void runEmail(targetOf(menu.item), false),
            remind: () => void runEmail(targetOf(menu.item), true),
            edit: () => { void openForEdit(party.id, menu.item.groupId, setEditing, refused); },
            rotate: () => setConfirm({ kind: 'rotate', target: targetOf(menu.item) }),
            remove: () => setConfirm({ kind: 'remove', target: targetOf(menu.item) }),
            close: () => setMenu(null),
          })}
        />
      )}

      {menu?.kind === 'other' && (
        <GuestActionSheet
          title={t('party.console.other.more', { name: menu.item.name })}
          testId="guest-other-sheet" onClose={() => setMenu(null)}
          actions={[
            {
              key: 'rename',
              label: t('party.console.other.rename'),
              testId: 'guest-other-rename',
              onSelect: () => { setRenaming(menu.item); setMenu(null); },
            },
            {
              key: 'remove',
              label: t('party.console.other.remove'),
              danger: true,
              testId: 'guest-other-remove',
              onSelect: () => { setConfirm({ kind: 'removeOther', item: menu.item }); setMenu(null); },
            },
          ]}
        />
      )}

      {confirm && (
        <Modal
          title={t(confirm.kind === 'rotate' ? 'party.console.action.rotate'
            : confirm.kind === 'remove' ? 'party.console.action.remove'
              : 'party.console.other.remove')}
          onClose={() => setConfirm(null)} testId="guest-confirm" className="guest-sheet" focusPanelOnOpen
          footer={(
            <>
              <button type="button" className="row-action" onClick={() => setConfirm(null)}>
                {t('party.console.cancel')}
              </button>
              <button
                type="button" className="row-action-primary row-action-danger" data-testid="guest-confirm-yes"
                onClick={() => {
                  const pending = confirm;
                  setConfirm(null);
                  setMenu(null);
                  if (pending.kind === 'rotate') void runRotate(pending.target);
                  else if (pending.kind === 'remove') void runRemove(pending.target);
                  else void removeOther(pending.item);
                }}
              >
                {t(confirm.kind === 'rotate' ? 'party.console.rotateConfirm'
                  : confirm.kind === 'remove' ? 'party.console.removeYes'
                    : 'party.console.other.removeYes')}
              </button>
            </>
          )}
        >
          <p role="alert">
            {confirm.kind === 'rotate' ? t('party.console.rotateHelp')
              : confirm.kind === 'remove' ? t('party.console.removeConfirm', { label: confirm.target.label })
                : t('party.console.other.removeConfirm', { name: confirm.item.name })}
          </p>
        </Modal>
      )}

      {editing && (
        <GuestGroupEditor
          partyId={party.id} group={editing === 'new' ? null : editing}
          onClose={() => setEditing(null)}
          onRefused={(err) => refused(err, 'party.console.editor.error.generic')}
          onSaved={(result, label) => {
            const created = editing === 'new';
            setEditing(null);
            setMenu(null);
            if (created) {
              // The new group belongs wherever its name puts it, so the list is
              // read again — and opened on the group the host just described.
              reload();
              if (result.groupId) openDetail(result.groupId);
              setNotice({ tone: 'ok', text: t('party.console.created', { label }) });
            } else if (result.groupId) {
              void refreshGroup(result.groupId).catch(() => reload());
              setNotice({
                tone: 'ok',
                text: result.linkRotated ? t('party.console.rotated') : t('party.console.saved'),
              });
            }
            void refreshSummary();
          }}
        />
      )}

      {adding && (
        <AddPersonSheet
          busy={Boolean(busy.add)} onClose={() => setAdding(false)}
          onSubmit={(name, clientRequestId) => void addPerson(name, clientRequestId)}
        />
      )}

      {renaming && (
        <RenameOtherSheet
          item={renaming} busy={Boolean(busy[`other:${renaming.id}`])}
          onClose={() => setRenaming(null)}
          onSubmit={(name) => void renameOther(renaming, name)}
        />
      )}
    </div>
  );
}

function targetOf(item: GuestDirectoryGroupItem): DetailTarget {
  return { groupId: item.groupId, label: item.label, version: item.version };
}

/** The editor needs the group whole — its people's addresses included — so it is read first. */
async function openForEdit(
  partyId: string,
  groupId: string,
  setEditing: (group: PartyInvitationGroup) => void,
  onRefused: (err: unknown, fallback: MessageKey) => void,
): Promise<void> {
  try {
    setEditing((await getPartyInvitationGroup(partyId, groupId)).group);
  } catch (err) {
    onRefused(err, 'party.console.editor.error.generic');
  }
}

function groupActions(item: GuestDirectoryGroupItem, handlers: {
  t: (key: MessageKey, params?: Record<string, string | number>) => string;
  open(): void;
  share(channel: InvitationShareChannel): void;
  email(): void;
  remind(): void;
  edit(): void;
  rotate(): void;
  remove(): void;
  close(): void;
}): GuestAction[] {
  const { t } = handlers;
  const then = (action: () => void) => () => { handlers.close(); action(); };
  const actions: GuestAction[] = [
    { key: 'open', label: t('party.console.action.details'), testId: 'guest-action-details', onSelect: then(handlers.open) },
  ];
  if (item.canShare) {
    actions.push({
      key: 'whatsapp',
      label: t('party.console.action.whatsapp'),
      testId: 'guest-action-whatsapp',
      onSelect: then(() => handlers.share('whatsapp')),
    });
  }
  if (item.canSend) {
    actions.push({
      key: 'email',
      label: t('party.console.action.emailMenu'),
      testId: 'guest-action-email',
      onSelect: then(handlers.email),
    });
  }
  if (item.canRemind) {
    actions.push({
      key: 'remind',
      label: t('party.console.action.remind'),
      testId: 'guest-action-remind',
      onSelect: then(handlers.remind),
    });
  }
  if (item.canShare) {
    actions.push({
      key: 'copy',
      label: t('party.console.action.copy'),
      testId: 'guest-action-copy',
      onSelect: then(() => handlers.share('copy')),
    });
  }
  actions.push(
    { key: 'edit', label: t('party.console.action.edit'), testId: 'guest-action-edit', onSelect: then(handlers.edit) },
    {
      key: 'rotate',
      label: t('party.console.action.rotate'),
      testId: 'guest-action-rotate',
      onSelect: then(handlers.rotate),
    },
    {
      key: 'remove',
      label: t('party.console.action.remove'),
      danger: true,
      testId: 'guest-action-remove',
      onSelect: then(handlers.remove),
    },
  );
  return actions;
}

/**
 * Nothing to show, and what that means: a party nobody was invited to is not
 * an incomplete one, and a search that matched nothing is not an empty list.
 */
function EmptyList({
  live, searching, query, onAddGroup, onAddPerson, onClear,
}: {
  live: boolean;
  searching: boolean;
  query: string;
  onAddGroup(): void;
  onAddPerson(): void;
  onClear(): void;
}) {
  const { t } = useI18n();

  if (searching) {
    return (
      <div className="party-card" data-testid="guest-no-results">
        <p>{query ? t('party.console.noResultsFor', { query }) : t('party.console.noResults')}</p>
        <button type="button" className="row-action" data-testid="guest-clear-filters" onClick={onClear}>
          {t('party.console.clearFilters')}
        </button>
      </div>
    );
  }
  if (live) {
    return (
      <div className="party-card" data-testid="guest-empty-arrivals">
        <p>{t('party.console.empty.arrivals')}</p>
        <button type="button" className="row-action-primary" data-testid="guest-empty-add" onClick={onAddPerson}>
          {t('party.console.addPerson')}
        </button>
      </div>
    );
  }
  return (
    <section className="party-card" data-testid="guest-open" aria-labelledby="guest-open-heading">
      <h3 id="guest-open-heading">{t('party.console.open.heading')}</h3>
      <p>{t('party.console.open.body')}</p>
      <p className="muted">{t('party.console.open.attendanceLater')}</p>
      <p className="muted">{t('party.console.open.offer')}</p>
      <p className="party-card-actions">
        <button type="button" className="row-action-primary" data-testid="guest-empty-add" onClick={onAddGroup}>
          {t('party.console.addGroup')}
        </button>
      </p>
    </section>
  );
}

/** A name and one button: recording somebody at the door is not an admission form. */
function AddPersonSheet({
  busy, onClose, onSubmit,
}: {
  busy: boolean;
  onClose(): void;
  onSubmit(name: string, clientRequestId: string): void;
}) {
  const { t } = useI18n();
  const [name, setName] = useState('');
  // ONE id per add, reused by that add's retries; typing a different name is a
  // different add.
  const [requestId, setRequestId] = useState(newClientRequestId);
  const valid = normalizeText(name) !== null && codePoints(name.trim()) <= PARTY_ATTENDANCE_LIMITS.name;

  return (
    <Modal
      title={t('party.console.add.title')} onClose={onClose} dismissable={!busy}
      testId="guest-add-sheet" className="guest-sheet guest-sheet--form"
      footer={(
        <>
          <button type="button" className="row-action" disabled={busy} onClick={onClose}>
            {t('party.console.cancel')}
          </button>
          <button
            type="submit" form="guest-add-form" className="row-action-primary"
            data-testid="guest-add-submit" disabled={busy || !valid}
          >
            {t('party.console.add.submit')}
          </button>
        </>
      )}
    >
      <form
        id="guest-add-form" className="guest-form"
        onSubmit={(e) => { e.preventDefault(); if (valid) onSubmit(name, requestId); }}
      >
        <label className="party-field">
          <span>{t('party.console.add.name')}</span>
          <input
            value={name} disabled={busy} autoComplete="off" data-testid="guest-add-name"
            aria-label={t('party.console.add.name')}
            onChange={(e) => { setName(e.target.value); setRequestId(newClientRequestId()); }}
          />
        </label>
        <p className="muted guest-form-help">{t('party.console.add.help')}</p>
      </form>
    </Modal>
  );
}

function RenameOtherSheet({
  item, busy, onClose, onSubmit,
}: {
  item: GuestDirectoryOtherItem;
  busy: boolean;
  onClose(): void;
  onSubmit(name: string): void;
}) {
  const { t } = useI18n();
  const [name, setName] = useState(item.name);
  const valid = normalizeText(name) !== null && codePoints(name.trim()) <= PARTY_ATTENDANCE_LIMITS.name;

  return (
    <Modal
      title={t('party.console.other.rename')} onClose={onClose} dismissable={!busy}
      testId="guest-rename-sheet" className="guest-sheet guest-sheet--form"
      footer={(
        <>
          <button type="button" className="row-action" disabled={busy} onClick={onClose}>
            {t('party.console.cancel')}
          </button>
          <button
            type="submit" form="guest-rename-form" className="row-action-primary"
            data-testid="guest-rename-submit" disabled={busy || !valid}
          >
            {t('party.console.other.save')}
          </button>
        </>
      )}
    >
      <form
        id="guest-rename-form" className="guest-form"
        onSubmit={(e) => { e.preventDefault(); if (valid) onSubmit(name); }}
      >
        <label className="party-field">
          <span>{t('party.console.add.name')}</span>
          <input
            value={name} disabled={busy} autoComplete="off" data-testid="guest-rename-name"
            aria-label={t('party.console.add.name')} onChange={(e) => setName(e.target.value)}
          />
        </label>
      </form>
    </Modal>
  );
}

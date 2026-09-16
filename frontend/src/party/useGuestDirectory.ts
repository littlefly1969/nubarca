import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  GUEST_DIRECTORY_LIMITS,
  queryPartyGuestDirectory,
  guestDirectoryItemKey,
  type GuestDirectoryGroupItem,
  type GuestDirectoryItem,
  type GuestDirectoryOtherItem,
  type GuestDirectoryState,
  type GuestDirectorySummary,
  type PartyAttendanceSummary,
} from '@nubarca/api-client';

// THE LIST THE CONSOLE HOLDS: the pages it has actually shown, and nothing
// else. A new search or filter is a NEW list — the server answers it from the
// first page, counts included — while scrolling appends the next page at the
// cursor the last one ended on, so a group added or renamed meanwhile never
// makes a page repeat or skip what is around it.
//
// Every mutation the console makes patches the item it changed IN PLACE rather
// than reloading: the host keeps their search, their filter and their position
// in the list, which is the whole point of paging it.
//
// The search itself never leaves this process except in the body of a POST: it
// is an argument to the hook, not a URL parameter, so nothing writes a guest's
// name to the address bar, the history or a log. See the console for why that
// costs a refresh.

export interface GuestDirectoryMeta {
  partyStatus: 'draft' | 'published' | 'live' | 'ended';
  mailAvailable: boolean;
  shareAvailable: boolean;
}

export interface GuestDirectoryView {
  status: 'loading' | 'ready' | 'error';
  items: GuestDirectoryItem[];
  summary: GuestDirectorySummary | null;
  meta: GuestDirectoryMeta | null;
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreFailed: boolean;
  loadMore(): void;
  reload(): void;
  patchGroup(item: GuestDirectoryGroupItem): void;
  patchOther(item: GuestDirectoryOtherItem): void;
  prependOther(item: GuestDirectoryOtherItem): void;
  removeItem(key: string): void;
  setSummary(summary: GuestDirectorySummary | null): void;
  /** An arrival changed the attendance counts and nothing that was declared. */
  patchAttendance(attendance: PartyAttendanceSummary): void;
}

interface Loaded {
  items: GuestDirectoryItem[];
  summary: GuestDirectorySummary | null;
  meta: GuestDirectoryMeta;
  cursor: string | null;
}

function merge(current: GuestDirectoryItem[], next: GuestDirectoryItem[]): GuestDirectoryItem[] {
  const seen = new Set(current.map(guestDirectoryItemKey));
  return [...current, ...next.filter((item) => !seen.has(guestDirectoryItemKey(item)))];
}

export function useGuestDirectory(
  partyId: string,
  query: { q: string; state: GuestDirectoryState },
  onUnauthorized: () => void,
): GuestDirectoryView {
  const { q, state } = query;
  const [loaded, setLoaded] = useState<Loaded | null>(null);
  const [status, setStatus] = useState<'loading' | 'ready' | 'error'>('loading');
  const [reloadKey, setReloadKey] = useState(0);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadMoreFailed, setLoadMoreFailed] = useState(false);
  // The cursor a "load more" must use, read outside React's render cycle so a
  // sentinel firing twice cannot ask for the same page twice.
  const cursorRef = useRef<string | null>(null);
  const inFlight = useRef(false);
  // Which list the pages belong to. A search answered after the host has typed
  // another one would otherwise append ITS rows and leave ITS cursor behind —
  // and the next page would be asked for with a cursor the server issued for a
  // list that is no longer on the screen.
  const generation = useRef(0);
  // The next page in flight, so a new search can CANCEL it rather than pay for
  // a response it is going to throw away.
  const moreCtrl = useRef<AbortController | null>(null);

  const failed = useCallback((err: unknown): boolean => {
    if (err instanceof ApiError && err.status === 401) {
      onUnauthorized();
      return true;
    }
    return false;
  }, [onUnauthorized]);

  useEffect(() => {
    const ctrl = new AbortController();
    generation.current += 1;
    cursorRef.current = null;
    inFlight.current = false;
    moreCtrl.current?.abort();
    moreCtrl.current = null;
    setStatus('loading');
    setLoadMoreFailed(false);
    setLoadingMore(false);
    queryPartyGuestDirectory(partyId, { q, state, take: GUEST_DIRECTORY_LIMITS.defaultTake }, ctrl.signal)
      .then((page) => {
        cursorRef.current = page.nextCursor;
        setLoaded({
          items: page.items,
          summary: page.summary,
          meta: {
            partyStatus: page.partyStatus,
            mailAvailable: page.mailAvailable,
            shareAvailable: page.shareAvailable,
          },
          cursor: page.nextCursor,
        });
        setStatus('ready');
      })
      .catch((err: unknown) => {
        if (ctrl.signal.aborted) return;
        if (!failed(err)) setStatus('error');
      });
    return () => ctrl.abort();
  }, [partyId, q, state, reloadKey, failed]);

  const loadMore = useCallback(() => {
    const cursor = cursorRef.current;
    if (!cursor || inFlight.current) return;
    const mine = generation.current;
    const ctrl = new AbortController();
    moreCtrl.current = ctrl;
    inFlight.current = true;
    setLoadingMore(true);
    setLoadMoreFailed(false);
    queryPartyGuestDirectory(
      partyId, { q, state, cursor, take: GUEST_DIRECTORY_LIMITS.defaultTake }, ctrl.signal)
      .then((page) => {
        // The list this page continues is gone; its rows and its cursor belong
        // to it, not to what the host is looking at now.
        if (mine !== generation.current) return;
        cursorRef.current = page.nextCursor;
        setLoaded((current) => (current
          ? { ...current, items: merge(current.items, page.items), cursor: page.nextCursor }
          : current));
      })
      .catch((err: unknown) => {
        // An abort is a new search, which bumps the generation: the same guard
        // covers both. A page that genuinely failed leaves the list ALONE and
        // offers the retry — losing what the host was reading because the next
        // page timed out would be a worse answer than not having it yet.
        if (mine !== generation.current) return;
        if (!failed(err)) setLoadMoreFailed(true);
      })
      .finally(() => {
        // A new search has already reset both; leave its state alone.
        if (mine !== generation.current) return;
        if (moreCtrl.current === ctrl) moreCtrl.current = null;
        inFlight.current = false;
        setLoadingMore(false);
      });
  }, [partyId, q, state, failed]);

  const update = useCallback((change: (current: Loaded) => Loaded) => {
    setLoaded((current) => (current ? change(current) : current));
  }, []);

  const patchGroup = useCallback((item: GuestDirectoryGroupItem) => {
    update((current) => ({
      ...current,
      items: current.items.map((existing) =>
        existing.kind === 'group' && existing.groupId === item.groupId ? item : existing),
    }));
  }, [update]);

  const patchOther = useCallback((item: GuestDirectoryOtherItem) => {
    update((current) => ({
      ...current,
      items: current.items.map((existing) =>
        existing.kind === 'other' && existing.id === item.id ? item : existing),
    }));
  }, [update]);

  // Other arrivals are listed latest first, so a person just recorded belongs
  // at the top — where the host can see that it worked.
  const prependOther = useCallback((item: GuestDirectoryOtherItem) => {
    update((current) => ({
      ...current,
      items: [item, ...current.items.filter((existing) =>
        !(existing.kind === 'other' && existing.id === item.id))],
    }));
  }, [update]);

  const removeItem = useCallback((key: string) => {
    update((current) => ({
      ...current,
      items: current.items.filter((existing) => guestDirectoryItemKey(existing) !== key),
    }));
  }, [update]);

  const setSummary = useCallback((summary: GuestDirectorySummary | null) => {
    update((current) => ({ ...current, summary }));
  }, [update]);

  const patchAttendance = useCallback((attendance: PartyAttendanceSummary) => {
    update((current) => ({
      ...current,
      summary: current.summary ? { ...current.summary, attendance } : current.summary,
    }));
  }, [update]);

  return {
    status,
    items: loaded?.items ?? [],
    summary: loaded?.summary ?? null,
    meta: loaded?.meta ?? null,
    hasMore: loaded?.cursor !== null && loaded?.cursor !== undefined,
    loadingMore,
    loadMoreFailed,
    loadMore,
    reload: useCallback(() => setReloadKey((n) => n + 1), []),
    patchGroup,
    patchOther,
    prependOther,
    removeItem,
    setSummary,
    patchAttendance,
  };
}

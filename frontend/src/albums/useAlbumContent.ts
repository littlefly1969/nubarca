import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, listAlbumContent, type AlbumContentItem } from '@nubarca/api-client';

// The curator's PAGED view of one album's content — the state behind
// AlbumSharedContentPanel, which the owner's album page and an Editor's shared
// album page both open. One hook, one protocol, no owner/editor fork.
//
// THE VERSION IS THE CONSISTENCY BOUNDARY. Every row held here was read at
// `version`. A continuation asks for the rows after the last one held, AT that
// version; a 409 means somebody else changed the album, and the rows are thrown
// away and read again from the top — a page of one version is never appended to
// rows of another.
//
// The caller's OWN confirmed edits are the one exception, and a safe one. The
// server claimed exactly `version` for the edit and answered `version + 1`, so
// the album at `version + 1` is precisely the rows held here with that edit
// applied. Applying it locally keeps the scroll position and re-reads nothing,
// and because the cursor is simply the last row held, paging continues
// correctly afterwards — even when the edit moved an item past the rows held.

export const ALBUM_CONTENT_PAGE_SIZE = 40;

export type AlbumContentPhase = 'loading' | 'ready' | 'gone' | 'error';

export interface AlbumContentState {
  phase: AlbumContentPhase;
  version: number;
  canEdit: boolean;
  coverFileItemId: string | null;
  // The WHOLE album, however little of it is held.
  totalCount: number;
  // A prefix of the album in its curated order: positions 0..items.length-1.
  items: AlbumContentItem[];
  hasMore: boolean;
  loadingMore: boolean;
  loadMoreFailed: boolean;
  // How many times the album has been read from the top. Each read is a new
  // list, so a view keyed on this starts it at the top — a counter rather than
  // the phase, because a fast answer can land in the same render as the
  // `loading` it ends.
  reads: number;
}

export interface AlbumContent extends AlbumContentState {
  reload(): void;
  loadMore(): void;
  // Called when an edit starts. A continuation still in flight was read at the
  // version the edit is about to replace, so it is cancelled and whatever it
  // would have answered — rows or a 409 — never lands.
  discardPendingPage(): void;
  applyMove(albumItemId: string, targetIndex: number, version: number): void;
  applyRemove(albumItemId: string, version: number, coverFileItemId: string | null): void;
  applyCover(coverFileItemId: string | null, version: number): void;
}

const LOADING: AlbumContentState = {
  phase: 'loading',
  version: 0,
  canEdit: false,
  coverFileItemId: null,
  totalCount: 0,
  items: [],
  hasMore: false,
  loadingMore: false,
  loadMoreFailed: false,
  reads: 0,
};

export function useAlbumContent(
  albumId: string,
  onAuthError: () => void,
  // The album changed under a scrolling curator and the rows were re-read.
  onChangedWhileBrowsing: () => void,
): AlbumContent {
  const [state, setState] = useState<AlbumContentState>(LOADING);
  const stateRef = useRef(state);
  useEffect(() => { stateRef.current = state; });
  const callbacksRef = useRef({ onAuthError, onChangedWhileBrowsing });
  useEffect(() => { callbacksRef.current = { onAuthError, onChangedWhileBrowsing }; });

  // The first-page read and the continuation in flight, if any. Each is also
  // its own identity: an answer whose request is no longer the current one is
  // dropped, even if it arrived before the cancellation did.
  const firstPageRef = useRef<AbortController | null>(null);
  const nextPageRef = useRef<AbortController | null>(null);

  const cancelNextPage = useCallback(() => {
    nextPageRef.current?.abort();
    nextPageRef.current = null;
  }, []);

  const reload = useCallback(() => {
    firstPageRef.current?.abort();
    cancelNextPage();
    const ctrl = new AbortController();
    firstPageRef.current = ctrl;
    setState((prev) => ({ ...LOADING, reads: prev.reads + 1 }));

    listAlbumContent(albumId, { limit: ALBUM_CONTENT_PAGE_SIZE }, ctrl.signal)
      .then((page) => {
        if (firstPageRef.current !== ctrl) return;
        firstPageRef.current = null;
        setState((prev) => ({
          phase: 'ready',
          version: page.version,
          canEdit: page.canEdit,
          coverFileItemId: page.coverFileItemId,
          totalCount: page.totalCount,
          items: page.items,
          hasMore: !!page.nextCursor,
          loadingMore: false,
          loadMoreFailed: false,
          reads: prev.reads,
        }));
      })
      .catch((err) => {
        if (firstPageRef.current !== ctrl || (err as Error).name === 'AbortError') return;
        firstPageRef.current = null;
        if (err instanceof ApiError && err.status === 401) { callbacksRef.current.onAuthError(); return; }
        setState((prev) => ({
          ...LOADING,
          reads: prev.reads,
          phase: err instanceof ApiError && err.status === 404 ? 'gone' : 'error',
        }));
      });
  }, [albumId, cancelNextPage]);

  useEffect(() => {
    reload();
    return () => {
      firstPageRef.current?.abort();
      nextPageRef.current?.abort();
    };
  }, [reload]);

  const loadMore = useCallback(() => {
    const held = stateRef.current;
    if (held.phase !== 'ready' || !held.hasMore || nextPageRef.current !== null) return;
    const ctrl = new AbortController();
    nextPageRef.current = ctrl;
    setState((prev) => ({ ...prev, loadingMore: true, loadMoreFailed: false }));

    listAlbumContent(albumId, {
      limit: ALBUM_CONTENT_PAGE_SIZE,
      // Resume after the LAST ROW HELD — which, after a local edit, need not be
      // the row the previous page ended on.
      cursor: held.items.at(-1)?.albumItemId ?? null,
      expectedVersion: held.version,
    }, ctrl.signal)
      .then((page) => {
        if (nextPageRef.current !== ctrl) return;
        nextPageRef.current = null;
        setState((prev) => {
          // By identity, never trusting the boundary: a row must not appear
          // twice even if two pages ever overlapped.
          const seen = new Set(prev.items.map((i) => i.albumItemId));
          return {
            ...prev,
            items: [...prev.items, ...page.items.filter((i) => !seen.has(i.albumItemId))],
            totalCount: page.totalCount,
            coverFileItemId: page.coverFileItemId,
            hasMore: !!page.nextCursor,
            loadingMore: false,
          };
        });
      })
      .catch((err) => {
        if (nextPageRef.current !== ctrl || (err as Error).name === 'AbortError') return;
        nextPageRef.current = null;
        if (err instanceof ApiError && err.status === 409) {
          // Somebody else changed the album mid-scroll. Start again at the
          // current version rather than stitch two versions together.
          callbacksRef.current.onChangedWhileBrowsing();
          reload();
          return;
        }
        if (err instanceof ApiError && err.status === 401) {
          setState((prev) => ({ ...prev, loadingMore: false }));
          callbacksRef.current.onAuthError();
          return;
        }
        if (err instanceof ApiError && err.status === 404) {
          setState((prev) => ({ ...LOADING, reads: prev.reads, phase: 'gone' }));
          return;
        }
        setState((prev) => ({ ...prev, loadingMore: false, loadMoreFailed: true }));
      });
  }, [albumId, reload]);

  const discardPendingPage = useCallback(() => {
    if (nextPageRef.current === null) return;
    cancelNextPage();
    setState((prev) => ({ ...prev, loadingMore: false }));
  }, [cancelNextPage]);

  // The three local edits below each describe the album at the version the
  // server returned for them; they also cancel any continuation in flight,
  // since that was read at the version they replace.

  const applyMove = useCallback((albumItemId: string, targetIndex: number, version: number) => {
    cancelNextPage();
    setState((prev) => {
      const moved = prev.items.find((i) => i.albumItemId === albumItemId);
      const items = prev.items.filter((i) => i.albumItemId !== albumItemId);
      // A destination past the rows held leaves the item out of them: those
      // rows are the album's first positions, it no longer holds one, and the
      // next page brings it in where it now belongs.
      if (moved && targetIndex < prev.items.length) items.splice(targetIndex, 0, moved);
      return { ...prev, version, items, loadingMore: false };
    });
  }, [cancelNextPage]);

  const applyRemove = useCallback(
    (albumItemId: string, version: number, coverFileItemId: string | null) => {
      cancelNextPage();
      setState((prev) => ({
        ...prev,
        version,
        coverFileItemId,
        totalCount: Math.max(0, prev.totalCount - 1),
        items: prev.items
          .filter((i) => i.albumItemId !== albumItemId)
          .map((i) => ({ ...i, isCover: i.fileItemId === coverFileItemId })),
        loadingMore: false,
      }));
    },
    [cancelNextPage],
  );

  const applyCover = useCallback((coverFileItemId: string | null, version: number) => {
    cancelNextPage();
    setState((prev) => ({
      ...prev,
      version,
      coverFileItemId,
      items: prev.items.map((i) => ({ ...i, isCover: i.fileItemId === coverFileItemId })),
      loadingMore: false,
    }));
  }, [cancelNextPage]);

  return {
    ...state,
    reload,
    loadMore,
    discardPendingPage,
    applyMove,
    applyRemove,
    applyCover,
  };
}

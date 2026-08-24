import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  listSharedAlbumItems,
  type SharedAlbumItem,
  type SharedAlbumItemKind,
} from '@nubarca/api-client';

// The recipient's half of a shared album: pages of items in the album's curated
// order, filtered by media kind.
//
// Kept apart from the owner's useMediaWorkspace on purpose. That hook speaks the
// owner's query vocabulary — library scope, People, similarity anchors, semantic
// search, sort fields — none of which exists for a recipient, and a shared
// variant of it would be a dozen conditionals guarding capabilities that must
// simply not be reachable. What the two DO share is the presentation: the same
// justified wall, the same viewer, the same Play.

export type SharedItemsPhase =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'loadingMore' }
  | { kind: 'error' }
  // The share is gone: revoked, declined, the album deleted, or the owner's
  // account disabled. Indistinguishable by design, and all the same thing to the
  // person looking at the screen.
  | { kind: 'unavailable' };

export interface SharedAlbumItemsState {
  items: SharedAlbumItem[];
  phase: SharedItemsPhase;
  hasMore: boolean;
  // The whole album, whatever kind is on screen.
  total: number;
  photoCount: number;
  videoCount: number;
  refresh(): void;
  loadMore(): void;
}

export function useSharedAlbumItems(
  albumId: string,
  kind: SharedAlbumItemKind,
  onAuthError: () => void,
): SharedAlbumItemsState {
  const [items, setItems] = useState<SharedAlbumItem[]>([]);
  const [phase, setPhase] = useState<SharedItemsPhase>({ kind: 'loading' });
  const [cursor, setCursor] = useState<string | null>(null);
  const [counts, setCounts] = useState({ total: 0, photoCount: 0, videoCount: 0 });
  const abortRef = useRef<AbortController | null>(null);
  // Bumped to force a reload of the same (album, kind).
  const [reloadToken, setReloadToken] = useState(0);
  // Guards against two overlapping "load the next page" requests, which an
  // observer that fires twice would otherwise start.
  const loadingMoreRef = useRef(false);

  useEffect(() => {
    abortRef.current?.abort();
    const ctrl = new AbortController();
    abortRef.current = ctrl;
    setPhase({ kind: 'loading' });
    setItems([]);
    setCursor(null);
    loadingMoreRef.current = false;

    listSharedAlbumItems(albumId, { kind }, ctrl.signal)
      .then((page) => {
        if (ctrl.signal.aborted) return;
        setItems(page.items);
        setCursor(page.nextCursor);
        setCounts({
          total: page.total, photoCount: page.photoCount, videoCount: page.videoCount,
        });
        setPhase({ kind: 'ready' });
      })
      .catch((err) => {
        if ((err as Error).name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) { onAuthError(); return; }
        setPhase({ kind: err instanceof ApiError && err.status === 404 ? 'unavailable' : 'error' });
      });

    return () => ctrl.abort();
  }, [albumId, kind, reloadToken, onAuthError]);

  const loadMore = useCallback(() => {
    if (cursor === null || loadingMoreRef.current) return;
    loadingMoreRef.current = true;
    setPhase({ kind: 'loadingMore' });
    listSharedAlbumItems(albumId, { kind, cursor })
      .then((page) => {
        // Append by identity, never by trusting the page boundary: a keyset
        // cursor should not repeat, and if it ever does the wall must not show
        // the same tile twice.
        setItems((prev) => {
          const seen = new Set(prev.map((i) => i.fileItemId));
          return [...prev, ...page.items.filter((i) => !seen.has(i.fileItemId))];
        });
        setCursor(page.nextCursor);
        setCounts({
          total: page.total, photoCount: page.photoCount, videoCount: page.videoCount,
        });
        setPhase({ kind: 'ready' });
      })
      .catch((err) => {
        if (err instanceof ApiError && err.status === 401) { onAuthError(); return; }
        // A membership that ended mid-scroll: the album is simply gone now.
        setPhase({ kind: err instanceof ApiError && err.status === 404 ? 'unavailable' : 'error' });
      })
      .finally(() => { loadingMoreRef.current = false; });
  }, [albumId, kind, cursor, onAuthError]);

  const refresh = useCallback(() => setReloadToken((n) => n + 1), []);

  return {
    items,
    phase,
    hasMore: cursor !== null,
    ...counts,
    refresh,
    loadMore,
  };
}

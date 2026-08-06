import type { TvAlbumItem } from '../api/tv';

// Helpers for live-refreshing an open party album's items (grid + slideshow).
// The server returns a stable list ordered ascending by AddedAt, so new guest
// uploads append to the end; existing items keep their position.

// True when both lists are the same items in the same order — lets a poll skip a
// state update (and re-render) when nothing changed.
export function sameItemIds(a: TvAlbumItem[], b: TvAlbumItem[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) {
    if (a[i].id !== b[i].id) return false;
  }
  return true;
}

// Given the freshly fetched list and the currently-shown item id, return the
// index that keeps the SAME item visible in the slideshow. Falls back to the
// clamped previous index when the current item is gone (e.g. owner removed it).
export function remapIndexById(
  items: TvAlbumItem[],
  currentId: string | undefined,
  previousIndex: number,
): number {
  if (items.length === 0) return 0;
  const found = currentId ? items.findIndex((it) => it.id === currentId) : -1;
  if (found >= 0) return found;
  return Math.min(Math.max(previousIndex, 0), items.length - 1);
}

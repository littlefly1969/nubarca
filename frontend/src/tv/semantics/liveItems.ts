// Live-refreshing a party album's items — a PORT of tv/src/lib/liveItems.ts.
// The server returns a stable list ordered ascending by AddedAt, so new guest
// uploads append and existing items keep their position.

import type { TvAlbumItem } from '@nubarca/api-client';

/** Same items in the same order: lets a poll skip a re-render when nothing changed. */
export function sameItemIds(a: TvAlbumItem[], b: TvAlbumItem[]): boolean {
  if (a.length !== b.length) return false;
  for (let i = 0; i < a.length; i += 1) {
    if (a[i].id !== b[i].id) return false;
  }
  return true;
}

/** The index that keeps the SAME item on screen after a refresh. */
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

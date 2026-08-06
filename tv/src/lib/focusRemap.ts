// Pure focus-restoration math for the TV grid. The remote's position is tracked
// by ITEM ID, not by a raw index, because the index is unstable: live Party
// uploads append items, the face filter swaps the display list, and a width
// change rebuilds the justified rows. On any of those transitions the focus must
// land back on the SAME photo when it still exists, else on the first item.

export interface HasId {
  id: string;
}

// The index in `nextItems` of the item that was focused in `prevItems`
// (identified by id). Falls back to 0 when that item is gone or nothing was
// focused, and clamps a stale index defensively.
export function remapFocusIndexById(
  prevItems: readonly HasId[],
  prevIndex: number,
  nextItems: readonly HasId[],
): number {
  if (nextItems.length === 0) return 0;
  const clampedPrev = Math.min(Math.max(0, prevIndex), Math.max(0, prevItems.length - 1));
  const focusedId = prevItems[clampedPrev]?.id;
  if (!focusedId) return 0;
  const next = nextItems.findIndex((it) => it.id === focusedId);
  return next >= 0 ? next : 0;
}

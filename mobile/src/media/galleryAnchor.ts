// Where a gallery should be looking.
//
// A gallery's position is an ITEM, never a pixel offset. Offsets stop meaning
// anything the moment the geometry changes: rotate the phone, the column count
// changes, React Native rebuilds the list, and the number that used to point at
// row nine now points somewhere else entirely — usually the top.
//
// So the anchor is a media id, and everything below is the arithmetic of
// turning that id back into a position under whatever geometry is now in force.
// Pure, because the interesting cases — the item was deleted, the query
// changed, the list is empty — are exactly the ones that are miserable to
// reproduce by rotating a device.

export interface AnchorableItem {
  id: string;
}

/**
 * Where the anchored item currently sits, or null if it is not in this list.
 *
 * Null is an ordinary answer, not a failure: a refresh or a filter change can
 * legitimately remove the item somebody was looking at, and the gallery should
 * then simply stay where it is rather than throw or jump to the top.
 */
export function anchorIndexOf(
  items: readonly AnchorableItem[],
  anchorId: string | null,
): number | null {
  if (anchorId === null) return null;
  const index = items.findIndex((item) => item.id === anchorId);
  return index === -1 ? null : index;
}

/** Which row an index falls on, under a given column count. */
export function rowOf(index: number, columns: number): number {
  if (columns <= 0) return 0;
  return Math.floor(index / columns);
}

/**
 * The item a gallery should treat as its position, given what is on screen.
 *
 * The FIRST visible item, not the middle one: it is the thing the user's eye is
 * anchored to at the top of the viewport, and restoring it puts the screen back
 * where they left it rather than half a row off.
 */
export function anchorFromVisible(visibleIds: readonly string[]): string | null {
  return visibleIds.length > 0 ? visibleIds[0] : null;
}

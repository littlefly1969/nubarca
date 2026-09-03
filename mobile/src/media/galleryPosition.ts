// Where a gallery is looking, and where it should look after the geometry
// changes under it.
//
// A position is an ITEM plus how far the viewport has travelled through that
// item's row. The identity survives a rotation — a pixel offset does not, since
// the same height is a different row under a different column count — and the
// progress keeps the restored view roughly where the eye left it rather than
// snapping to a row boundary.
//
// Everything here is arithmetic on numbers, so the cases that matter (the last
// partial row, a column count that changes, an item that no longer exists) are
// decided in a test rather than on a device.

export interface GalleryGeometry {
  columns: number;
  /** Distance from one row's top to the next: tile plus one seam. */
  rowExtent: number;
  /** Content padding above the first row. */
  topPadding: number;
}

export interface GalleryPositionAnchor {
  /** The first item of the leading visible row. */
  itemId: string;
  /** How far the viewport has travelled through that row: 0 <= p < 1. */
  rowProgress: number;
}

export interface IdentifiedItem {
  id: string;
}

/**
 * The anchor implied by a scroll offset.
 *
 * Returns null when the list is empty or the geometry is degenerate — an
 * ordinary answer while a gallery is still loading, not a failure.
 */
export function anchorFromScroll(input: {
  y: number;
  geometry: GalleryGeometry;
  items: readonly IdentifiedItem[];
}): GalleryPositionAnchor | null {
  const { y, geometry, items } = input;
  const { columns, rowExtent, topPadding } = geometry;
  if (items.length === 0 || columns <= 0 || rowExtent <= 0) return null;

  // Above the first row — including overscroll, which reports negatives — the
  // gallery is at its start.
  const travelled = y - topPadding;
  if (travelled <= 0) return { itemId: items[0].id, rowProgress: 0 };

  const rowIndex = Math.floor(travelled / rowExtent);
  const firstItemIndex = rowIndex * columns;
  // Past the end (overscroll at the bottom) the last row is still the answer.
  if (firstItemIndex >= items.length) {
    const lastRowFirst = Math.floor((items.length - 1) / columns) * columns;
    return { itemId: items[lastRowFirst].id, rowProgress: 0 };
  }
  return {
    itemId: items[firstItemIndex].id,
    rowProgress: (travelled - rowIndex * rowExtent) / rowExtent,
  };
}

/**
 * Where to scroll so that an anchor is where it was, under new geometry.
 *
 * Returns null when the anchored item is not in the list. That is an ordinary
 * answer too: a refresh or a filter can remove what somebody was looking at,
 * and the gallery should then stay where it is rather than jump to the top.
 */
export function offsetForAnchor(input: {
  anchor: GalleryPositionAnchor;
  geometry: GalleryGeometry;
  items: readonly IdentifiedItem[];
}): number | null {
  const { anchor, geometry, items } = input;
  const { columns, rowExtent, topPadding } = geometry;
  if (columns <= 0 || rowExtent <= 0) return null;

  const itemIndex = items.findIndex((item) => item.id === anchor.itemId);
  if (itemIndex === -1) return null;

  const rowIndex = Math.floor(itemIndex / columns);
  const progress = Number.isFinite(anchor.rowProgress)
    ? Math.min(Math.max(anchor.rowProgress, 0), 1)
    : 0;
  return Math.max(0, topPadding + rowIndex * rowExtent + progress * rowExtent);
}

/**
 * Whether two geometries would lay the same content out differently.
 *
 * A rotation that changes only the width still changes the tile size, and
 * therefore the row extent — so this compares the numbers that decide layout
 * rather than the orientation that caused them to change.
 */
export function geometryChanged(a: GalleryGeometry | null, b: GalleryGeometry): boolean {
  if (a === null) return false;
  return (
    a.columns !== b.columns || a.rowExtent !== b.rowExtent || a.topPadding !== b.topPadding
  );
}

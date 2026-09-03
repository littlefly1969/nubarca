// A gallery's rows, as data.
//
// WHY THIS EXISTS. `FlatList` with `numColumns` is not a flat list of items: it
// converts internally into a VirtualizedList of ROWS, so its index space is
// rows and its public `scrollToIndex` forwards the index straight through.
// Passing a media index into it is out of range by construction — 120 items in
// 3 columns is 40 rows, and item 73 addresses nothing. That was a crash path,
// not a timing problem, and no amount of retrying could have fixed it.
//
// So the rows become explicit and the list becomes ordinary: one column, one
// item per row, geometry we state rather than infer. Position is then a pixel
// offset we can compute, which is defined for every item at every geometry.
//
// Pure, with no React import, because every interesting case here is an
// arithmetic boundary — the last partial row, the page boundary, a column count
// that changes under a rotation — and none of them should need a device.

export interface GalleryRow<T> {
  rowIndex: number;
  /** Index in the flat item list of this row's first item. */
  firstItemIndex: number;
  items: readonly T[];
}

/** Group items into rows, in order, losing and duplicating nothing. */
export function buildGalleryRows<T>(items: readonly T[], columns: number): GalleryRow<T>[] {
  if (columns <= 0 || items.length === 0) return [];
  const rows: GalleryRow<T>[] = [];
  for (let start = 0; start < items.length; start += columns) {
    rows.push({
      rowIndex: rows.length,
      firstItemIndex: start,
      // The final row is allowed to be short; padding it with blanks would put
      // fake items into a list that reports its own length.
      items: items.slice(start, start + columns),
    });
  }
  return rows;
}

export function rowForItemIndex(itemIndex: number, columns: number): number {
  if (columns <= 0) return 0;
  return Math.floor(Math.max(0, itemIndex) / columns);
}

export function rowCountFor(itemCount: number, columns: number): number {
  if (columns <= 0 || itemCount <= 0) return 0;
  return Math.ceil(itemCount / columns);
}

/**
 * The vertical distance from one row's top to the next.
 *
 * The tile plus one seam. The final row's seam is not drawn, but it is still
 * part of the extent used for layout: a list whose declared geometry disagrees
 * with what it renders scrolls to the wrong place, and the error accumulates
 * with every row above.
 */
export function rowExtent(tileSize: number, gap: number): number {
  return tileSize + gap;
}

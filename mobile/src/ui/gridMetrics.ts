// How a gallery divides its width.
//
// Deliberately free of imports: the geometry has to be checkable in a plain
// Node test, and `tokens.ts` pulls in the font bundle. The gap is therefore a
// parameter rather than a lookup — callers pass `grid.gap`.
//
// EVERY SEAM IS THE SAME. That sounds automatic and is not: a FlatList with
// `numColumns` lays each row out with `flex-start`, so without an explicit
// column gap the tiles butt together and the entire rounding remainder lands
// against the right edge. Measured on a device, that was 5 px of inset on the
// left against 19 px on the right, with column pairs touching.
//
// The remainder is split between the two outer insets, which is the only place
// it can go without making one seam differ from another.

export interface GridMetrics {
  /** Edge length of a square tile. */
  tileSize: number;
  /** Outer inset on each side, gap plus its share of the remainder. */
  sidePadding: number;
}

export function gridMetrics(
  width: number,
  horizontalInsets: number,
  columns: number,
  gap: number,
): GridMetrics {
  if (columns <= 0) return { tileSize: 0, sidePadding: gap };
  const usable = Math.max(0, width - horizontalInsets);
  // One seam between each pair of columns, plus the two outer ones.
  const forTiles = usable - gap * (columns + 1);
  const tileSize = Math.max(0, Math.floor(forTiles / columns));
  const leftover = Math.max(0, forTiles - tileSize * columns);
  return { tileSize, sidePadding: gap + Math.floor(leftover / 2) };
}

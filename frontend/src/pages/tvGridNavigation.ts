import type { JustifiedLayoutRow } from '../media/layout/computeJustifiedRows';

export type TvGridDirection = 'left' | 'right' | 'up' | 'down';

// Spatial D-pad / keyboard navigation over a JUSTIFIED grid (variable tiles per
// row), used by the browser /tv fallback. Linear next/prev is wrong once rows
// have different tile counts, so:
//   RIGHT → next tile in the same row
//   LEFT  → previous tile in the same row
//   DOWN  → the tile in the next row whose horizontal CENTRE is nearest
//   UP    → the tile in the previous row whose horizontal CENTRE is nearest
// No wrap between the first and last tile. Returns the target item id, or null
// when there is nowhere to go in that direction (focus stays put). Pure — it
// reads only the computed layout, never the DOM.
export function findNextTvGridItem(
  rows: JustifiedLayoutRow[],
  gap: number,
  currentId: string,
  direction: TvGridDirection,
): string | null {
  // Locate the current tile: its row, its column, and its horizontal centre.
  let rowIndex = -1;
  let colIndex = -1;
  for (let r = 0; r < rows.length; r += 1) {
    const c = rows[r].items.findIndex((t) => t.id === currentId);
    if (c >= 0) { rowIndex = r; colIndex = c; break; }
  }
  if (rowIndex < 0) return null;

  const row = rows[rowIndex];
  if (direction === 'right') {
    return colIndex + 1 < row.items.length ? row.items[colIndex + 1].id : null;
  }
  if (direction === 'left') {
    return colIndex > 0 ? row.items[colIndex - 1].id : null;
  }

  const targetRow = direction === 'down' ? rowIndex + 1 : rowIndex - 1;
  if (targetRow < 0 || targetRow >= rows.length) return null;

  const centre = tileCentre(row, colIndex, gap);
  const candidates = rows[targetRow].items;
  let bestId: string | null = null;
  let bestDist = Number.POSITIVE_INFINITY;
  for (let c = 0; c < candidates.length; c += 1) {
    const dist = Math.abs(tileCentre(rows[targetRow], c, gap) - centre);
    if (dist < bestDist) { bestDist = dist; bestId = candidates[c].id; }
  }
  return bestId;
}

// Horizontal centre of a tile = sum of preceding tile widths + gaps + half its
// own width. Absolute origin is irrelevant (every row starts at 0), so a plain
// left-to-right accumulation is enough for the nearest-centre comparison.
function tileCentre(row: JustifiedLayoutRow, col: number, gap: number): number {
  let x = 0;
  for (let i = 0; i < col; i += 1) {
    x += row.items[i].width + gap;
  }
  return x + row.items[col].width / 2;
}

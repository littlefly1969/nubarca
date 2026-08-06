// Pure, framework-free justified-rows layout for the TV grid (Flickr/Google
// Photos style): every full row spans the content width exactly, tiles keep
// their aspect ratio (never cropped by the layout), and the original order is
// preserved. Mirrors the web gallery's `computeJustifiedRows`; the TV app is a
// separate package and cannot import SPA sources, so keep the two in sync.

export interface TvJustifiedTile<T> {
  item: T;
  originalIndex: number;
  width: number;
  height: number;
}

export interface TvJustifiedRow<T> {
  key: string;
  height: number;
  tiles: TvJustifiedTile<T>[];
  isLast: boolean;
}

export interface BuildTvJustifiedRowsOptions<T> {
  items: readonly T[];
  contentWidth: number;
  targetRowHeight: number;
  gap: number;
  // Optional gap used only while deciding how many items belong in a row.
  // Keeping the former, wider packing gap while rendering a smaller visual gap
  // preserves density and distributes the recovered width to the same tiles.
  packingGap?: number;
  getAspectRatio: (item: T) => number;
  // Stable per-item id so a row key never collapses to a bare index (identity
  // survives progressive loading / live Party appends).
  getId: (item: T) => string;
  // Optional clamp so a full row is never stretched taller than this before the
  // last row is left-aligned instead (defaults to 1.6× the target).
  maxRowHeight?: number;
}

const FALLBACK_ASPECT_RATIO = 1;

function safeAspectRatio(ratio: number): number {
  return Number.isFinite(ratio) && ratio > 0 ? ratio : FALLBACK_ASPECT_RATIO;
}

function buildRow<T>(
  items: T[],
  ratios: number[],
  originalIndices: number[],
  rowHeight: number,
  gap: number,
  justifiedWidth: number | null,
  getId: (item: T) => string,
  isLast: boolean,
): TvJustifiedRow<T> {
  const height = Math.max(1, Math.round(rowHeight));
  const tiles: TvJustifiedTile<T>[] = items.map((item, i) => ({
    item,
    originalIndex: originalIndices[i],
    width: Math.max(1, Math.round(ratios[i] * rowHeight)),
    height,
  }));

  if (justifiedWidth !== null && tiles.length > 0) {
    // Push the per-tile rounding residual onto the last tile so widths + gaps
    // sum to exactly the content width for a JUSTIFIED row.
    const totalGap = gap * (items.length - 1);
    const target = justifiedWidth - totalGap;
    const summed = tiles.reduce((acc, tile) => acc + tile.width, 0);
    const last = tiles[tiles.length - 1];
    last.width = Math.max(1, last.width + (target - summed));
  }

  return {
    key: `row-${getId(items[0])}-${items.length}`,
    height,
    tiles,
    isLast,
  };
}

export function buildTvJustifiedRows<T>(
  options: BuildTvJustifiedRowsOptions<T>,
): TvJustifiedRow<T>[] {
  const { items, getAspectRatio, getId } = options;
  if (items.length === 0) return [];

  const gap = Math.max(0, options.gap);
  const packingGap = Math.max(0, options.packingGap ?? gap);
  const contentWidth = Math.max(1, options.contentWidth);
  const target = Math.max(1, options.targetRowHeight);
  const maxRowHeight = Math.max(target, options.maxRowHeight ?? target * 1.6);

  const rows: TvJustifiedRow<T>[] = [];
  let rowItems: T[] = [];
  let rowRatios: number[] = [];
  let rowIndices: number[] = [];
  let ratioSum = 0;

  const rowHeightFor = (count: number, sum: number, rowGap: number) =>
    (contentWidth - rowGap * (count - 1)) / sum;

  items.forEach((item, index) => {
    const ratio = safeAspectRatio(getAspectRatio(item));
    rowItems.push(item);
    rowRatios.push(ratio);
    rowIndices.push(index);
    ratioSum += ratio;

    const rowHeight = rowHeightFor(rowItems.length, ratioSum, packingGap);
    // Close the row justified once filling the width no longer overshoots the
    // target height.
    if (rowHeight <= target) {
      const renderedHeight = rowHeightFor(rowItems.length, ratioSum, gap);
      rows.push(buildRow(rowItems, rowRatios, rowIndices, renderedHeight, gap, contentWidth, getId, false));
      rowItems = [];
      rowRatios = [];
      rowIndices = [];
      ratioSum = 0;
    }
  });

  // Leftover items: an incomplete last row. If filling the width would make it
  // taller than the max, lay it out LEFT-ALIGNED at the target height (never
  // stretch a couple of tiles across the whole width); otherwise justify it.
  if (rowItems.length > 0) {
    const packedHeight = rowHeightFor(rowItems.length, ratioSum, packingGap);
    if (packedHeight > maxRowHeight) {
      rows.push(buildRow(rowItems, rowRatios, rowIndices, Math.min(target, maxRowHeight), gap, null, getId, true));
    } else {
      const renderedHeight = rowHeightFor(rowItems.length, ratioSum, gap);
      rows.push(buildRow(rowItems, rowRatios, rowIndices, renderedHeight, gap, contentWidth, getId, true));
    }
  }

  if (rows.length > 0) {
    rows[rows.length - 1].isLast = true;
  }
  return rows;
}

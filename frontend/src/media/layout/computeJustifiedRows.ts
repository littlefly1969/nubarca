// Pure, framework-free justified-rows layout for the media wall (Flickr/Google
// Photos style): every full row spans the container width exactly, tiles keep
// their aspect ratio (never cropped by the layout), and the original order is
// preserved. The MediaGrid component owns virtualization and rendering; this
// file owns only the geometry so it can be unit-tested in isolation and can
// never reach into backend details.

export interface JustifiedLayoutItem {
  id: string;
  originalIndex: number;
  aspectRatio: number;
}

export interface JustifiedLayoutOptions {
  containerWidth: number;
  gap: number;
  targetRowHeight: number;
  minRowHeight: number;
  maxRowHeight: number;
}

export interface JustifiedLayoutTile {
  id: string;
  originalIndex: number;
  width: number;
  height: number;
}

export interface JustifiedLayoutRow {
  key: string;
  height: number;
  width: number;
  isLastRow: boolean;
  items: JustifiedLayoutTile[];
}

// A missing / zero / negative / non-finite ratio would produce zero or negative
// tile widths, so every candidate ratio is normalized to a safe square first.
const FALLBACK_ASPECT_RATIO = 1;

function safeAspectRatio(ratio: number): number {
  return Number.isFinite(ratio) && ratio > 0 ? ratio : FALLBACK_ASPECT_RATIO;
}

// Distribute a shared row height across the row's items, rounding each tile to
// whole pixels and pushing the accumulated rounding residual onto the last tile
// so the widths + gaps of a JUSTIFIED row sum to exactly the container width.
function buildRow(
  items: JustifiedLayoutItem[],
  ratios: number[],
  rowHeight: number,
  gap: number,
  justifiedWidth: number | null,
  isLastRow: boolean,
): JustifiedLayoutRow {
  const height = Math.max(1, Math.round(rowHeight));
  const tiles: JustifiedLayoutTile[] = items.map((item, i) => ({
    id: item.id,
    originalIndex: item.originalIndex,
    width: Math.max(1, Math.round(ratios[i] * rowHeight)),
    height,
  }));

  const totalGap = gap * (items.length - 1);
  if (justifiedWidth !== null && tiles.length > 0) {
    // Force the tile widths to fill exactly `justifiedWidth` by correcting the
    // last tile with whatever the per-tile rounding left over. Guard against a
    // degenerate (<=0) correction on pathologically narrow containers.
    const target = justifiedWidth - totalGap;
    const summed = tiles.reduce((acc, tile) => acc + tile.width, 0);
    const last = tiles[tiles.length - 1];
    last.width = Math.max(1, last.width + (target - summed));
  }

  const width = tiles.reduce((acc, tile) => acc + tile.width, 0) + totalGap;
  return {
    // Stable across re-renders while the row's leading item and length hold, so
    // React/virtualization keep row identity during progressive loading.
    key: `row-${items[0].originalIndex}-${items.length}`,
    height,
    width,
    isLastRow,
    items: tiles,
  };
}

export function computeJustifiedRows(
  items: JustifiedLayoutItem[],
  options: JustifiedLayoutOptions,
): JustifiedLayoutRow[] {
  if (items.length === 0) return [];

  const gap = Math.max(0, options.gap);
  // A non-positive width would make every height infinite; clamp to 1 so the
  // helper degrades gracefully (the component supplies a sane fallback width).
  const containerWidth = Math.max(1, options.containerWidth);
  const target = Math.max(1, options.targetRowHeight);
  const minRowHeight = Math.max(1, options.minRowHeight);
  const maxRowHeight = Math.max(minRowHeight, options.maxRowHeight);

  const rows: JustifiedLayoutRow[] = [];
  let rowItems: JustifiedLayoutItem[] = [];
  let rowRatios: number[] = [];
  let ratioSum = 0;

  // The height at which the current row's items exactly span the container.
  const rowHeightFor = (count: number, sum: number) =>
    (containerWidth - gap * (count - 1)) / sum;

  for (const item of items) {
    const ratio = safeAspectRatio(item.aspectRatio);
    rowItems.push(item);
    rowRatios.push(ratio);
    ratioSum += ratio;

    const rowHeight = rowHeightFor(rowItems.length, ratioSum);
    // Once the row is wide enough that filling the width no longer overshoots
    // the target height, close it justified at its natural (exact-fit) height.
    if (rowHeight <= target) {
      rows.push(buildRow(rowItems, rowRatios, rowHeight, gap, containerWidth, false));
      rowItems = [];
      rowRatios = [];
      ratioSum = 0;
    }
  }

  // Leftover items form the last row. If they would have to grow taller than the
  // max to fill the width, the row is clearly incomplete: lay it out left-
  // aligned at the target height instead of stretching a few tiles across the
  // whole width. Otherwise it fills the width like any complete row.
  if (rowItems.length > 0) {
    const naturalHeight = rowHeightFor(rowItems.length, ratioSum);
    if (naturalHeight > maxRowHeight) {
      const height = Math.min(target, maxRowHeight);
      rows.push(buildRow(rowItems, rowRatios, height, gap, null, true));
    } else {
      rows.push(buildRow(rowItems, rowRatios, naturalHeight, gap, containerWidth, true));
    }
  }

  // Flag the visually-last row even when it happens to be complete, so the
  // renderer can drop the trailing inter-row gap without re-deriving it.
  if (rows.length > 0) {
    rows[rows.length - 1].isLastRow = true;
  }
  return rows;
}

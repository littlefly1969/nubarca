export const TV_MEDIA_GRID_GAP = 4;
const TV_MEDIA_GRID_PACKING_GAP = 12;
export const TV_MEDIA_GRID_FOCUS_BLEED = 4;
// One shared virtualization budget for every TV media wall. Five initial rows
// fill the screen plus the immediate destination; three viewports keep one
// screen above and below without mounting dozens of off-screen image views.
export const TV_MEDIA_GRID_INITIAL_ROWS = 5;
export const TV_MEDIA_GRID_BATCH_ROWS = 4;
export const TV_MEDIA_GRID_WINDOW_SIZE = 3;

export interface TvMediaGridTile<T> {
  item: T;
  originalIndex: number;
  width: number;
  height: number;
}

export interface TvMediaGridRow<T> {
  key: string;
  height: number;
  tiles: TvMediaGridTile<T>[];
  isLast: boolean;
}

interface BuildOptions<T> {
  items: readonly T[];
  contentWidth: number;
  targetRowHeight: number;
  getAspectRatio: (item: T) => number;
  getId: (item: T) => string;
}

const safeRatio = (ratio: number) => (
  Number.isFinite(ratio) && ratio > 0 ? ratio : 1
);

export function tvMediaGridTargetHeight(surfaceHeight: number): number {
  return Math.min(Math.max(Math.round(surfaceHeight * 0.18), 125), 185);
}

export function buildTvMediaGridRows<T>({
  items,
  contentWidth: rawWidth,
  targetRowHeight: rawTarget,
  getAspectRatio,
  getId,
}: BuildOptions<T>): TvMediaGridRow<T>[] {
  if (items.length === 0) return [];

  const contentWidth = Math.max(1, rawWidth);
  const target = Math.max(1, rawTarget);
  const rows: TvMediaGridRow<T>[] = [];
  let pending: Array<{ item: T; index: number; ratio: number }> = [];
  let ratioSum = 0;

  const rowHeight = (gap: number) => (
    (contentWidth - gap * Math.max(0, pending.length - 1)) / ratioSum
  );

  const flush = (height: number, justify: boolean) => {
    const roundedHeight = Math.max(1, Math.round(height));
    const tiles = pending.map(({ item, index, ratio }) => ({
      item,
      originalIndex: index,
      width: Math.max(1, Math.round(ratio * height)),
      height: roundedHeight,
    }));
    if (justify) {
      const available = contentWidth - TV_MEDIA_GRID_GAP * Math.max(0, tiles.length - 1);
      const used = tiles.reduce((sum, tile) => sum + tile.width, 0);
      tiles[tiles.length - 1].width = Math.max(1, tiles[tiles.length - 1].width + available - used);
    }
    rows.push({
      // Appends may add tiles to the previous partial row. Its first item does
      // not change, so this key preserves the native FlatList cell, its child
      // image URIs, and the last-focused cell identity while geometry updates.
      key: getId(pending[0].item),
      height: roundedHeight,
      tiles,
      isLast: false,
    });
    pending = [];
    ratioSum = 0;
  };

  items.forEach((item, index) => {
    const ratio = safeRatio(getAspectRatio(item));
    pending.push({ item, index, ratio });
    ratioSum += ratio;
    if (rowHeight(TV_MEDIA_GRID_PACKING_GAP) <= target) {
      flush(rowHeight(TV_MEDIA_GRID_GAP), true);
    }
  });

  if (pending.length > 0) {
    const packed = rowHeight(TV_MEDIA_GRID_PACKING_GAP);
    const justify = packed <= target * 1.6;
    flush(justify ? rowHeight(TV_MEDIA_GRID_GAP) : target, justify);
  }
  rows[rows.length - 1].isLast = true;
  return rows;
}

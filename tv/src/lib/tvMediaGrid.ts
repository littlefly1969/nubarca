export const TV_MEDIA_GRID_GAP = 4;
export const TV_MEDIA_GRID_PACKING_GAP = 12;
export const TV_MEDIA_GRID_FOCUS_BLEED = 4;

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
      key: pending.map(({ item }) => getId(item)).join('|'),
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

export type TvMediaGridDirection = 'left' | 'right' | 'up' | 'down';

export interface TvMediaGridLinks {
  left?: string;
  right?: string;
  up?: string;
  down?: string;
}

export interface TvMediaGridModel {
  links: ReadonlyMap<string, TvMediaGridLinks>;
  rowKeyById: ReadonlyMap<string, string>;
}

interface PositionedTile<T> {
  tile: TvMediaGridTile<T>;
  start: number;
  end: number;
  center: number;
}

function positionedRow<T>(row: TvMediaGridRow<T>): PositionedTile<T>[] {
  let x = 0;
  return row.tiles.map((tile) => {
    const start = x;
    const end = start + tile.width;
    x = end + TV_MEDIA_GRID_GAP;
    return { tile, start, end, center: (start + end) / 2 };
  });
}

function nearestAt<T>(row: readonly PositionedTile<T>[], x: number): PositionedTile<T> | undefined {
  return row.reduce<PositionedTile<T> | undefined>((best, candidate) => {
    if (!best) return candidate;
    const distance = x < candidate.start ? candidate.start - x : x > candidate.end ? x - candidate.end : 0;
    const bestDistance = x < best.start ? best.start - x : x > best.end ? x - best.end : 0;
    if (distance !== bestDistance) return distance < bestDistance ? candidate : best;
    return Math.abs(candidate.center - x) < Math.abs(best.center - x) ? candidate : best;
  }, undefined);
}

export function buildTvMediaGridModel<T>(
  rows: readonly TvMediaGridRow<T>[],
  getId: (item: T) => string,
): TvMediaGridModel {
  const positioned = rows.map(positionedRow);
  const links = new Map<string, TvMediaGridLinks>();
  const rowKeyById = new Map<string, string>();

  positioned.forEach((row, rowIndex) => {
    row.forEach((source, tileIndex) => {
      const id = getId(source.tile.item);
      rowKeyById.set(id, rows[rowIndex].key);
      links.set(id, {
        left: row[tileIndex - 1] ? getId(row[tileIndex - 1].tile.item) : undefined,
        right: row[tileIndex + 1] ? getId(row[tileIndex + 1].tile.item) : undefined,
        up: rowIndex > 0
          ? getId(nearestAt(positioned[rowIndex - 1], source.center)!.tile.item)
          : undefined,
        down: rowIndex < positioned.length - 1
          ? getId(nearestAt(positioned[rowIndex + 1], source.center)!.tile.item)
          : undefined,
      });
    });
  });

  return { links, rowKeyById };
}

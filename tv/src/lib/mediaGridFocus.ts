import { createRef, useCallback, useMemo, useRef, type RefObject } from 'react';
import type { View } from 'react-native';
import type { TvJustifiedRow, TvJustifiedTile } from './justifiedMediaRows.ts';

export interface TvMediaFocusLinks {
  left?: string;
  right?: string;
  up?: string;
  down?: string;
}

export interface TvMediaFocusTargets {
  self: RefObject<View | null>;
  left?: RefObject<View | null>;
  right?: RefObject<View | null>;
  up?: RefObject<View | null>;
  down?: RefObject<View | null>;
}

interface PositionedTile<T extends { id: string }> {
  tile: TvJustifiedTile<T>;
  start: number;
  end: number;
  center: number;
}

function positionRow<T extends { id: string }>(
  row: TvJustifiedRow<T>,
  gap: number,
): PositionedTile<T>[] {
  let x = 0;
  return row.tiles.map((tile) => {
    const start = x;
    const end = start + tile.width;
    x = end + gap;
    return { tile, start, end, center: (start + end) / 2 };
  });
}

function verticalTarget<T extends { id: string }>(
  source: PositionedTile<T>,
  candidates: PositionedTile<T>[],
): string | undefined {
  let best: PositionedTile<T> | undefined;
  let bestOverlap = -1;
  let bestCenterDistance = Number.POSITIVE_INFINITY;

  for (const candidate of candidates) {
    const overlap = Math.max(
      0,
      Math.min(source.end, candidate.end) - Math.max(source.start, candidate.start),
    );
    const centerDistance = Math.abs(source.center - candidate.center);
    if (
      overlap > bestOverlap
      || (overlap === bestOverlap && centerDistance < bestCenterDistance)
      || (
        overlap === bestOverlap
        && centerDistance === bestCenterDistance
        && candidate.tile.originalIndex < (best?.tile.originalIndex ?? Number.POSITIVE_INFINITY)
      )
    ) {
      best = candidate;
      bestOverlap = overlap;
      bestCenterDistance = centerDistance;
    }
  }

  return best?.tile.item.id;
}

// Android's geometric focus search becomes ambiguous when adjacent justified
// rows contain differently sized tiles. Pin every D-pad move to one adjacent
// row (maximum horizontal overlap, then nearest centre) so DOWN can never jump
// over a row or be redirected to a remounted tile above the current one.
export function buildTvMediaFocusLinks<T extends { id: string }>(
  rows: readonly TvJustifiedRow<T>[],
  gap: number,
): Map<string, TvMediaFocusLinks> {
  const positioned = rows.map((row) => positionRow(row, Math.max(0, gap)));
  const links = new Map<string, TvMediaFocusLinks>();

  positioned.forEach((row, rowIndex) => {
    row.forEach((source, tileIndex) => {
      links.set(source.tile.item.id, {
        left: row[tileIndex - 1]?.tile.item.id,
        right: row[tileIndex + 1]?.tile.item.id,
        up: rowIndex > 0 ? verticalTarget(source, positioned[rowIndex - 1]) : undefined,
        down: rowIndex < positioned.length - 1
          ? verticalTarget(source, positioned[rowIndex + 1])
          : undefined,
      });
    });
  });

  return links;
}

// Ref registry shared by the three native media walls. Refs remain stable when
// a FlatList row is clipped/remounted, while the link map follows the current
// row geometry after paging, filtering or a surface-size change.
export function useTvMediaGridFocus<T extends { id: string }>(
  rows: readonly TvJustifiedRow<T>[],
  gap: number,
): (id: string) => TvMediaFocusTargets {
  const refs = useRef(new Map<string, RefObject<View | null>>());
  for (const row of rows) {
    for (const tile of row.tiles) {
      if (!refs.current.has(tile.item.id)) {
        refs.current.set(tile.item.id, createRef<View>());
      }
    }
  }

  const links = useMemo(() => buildTvMediaFocusLinks(rows, gap), [rows, gap]);

  return useCallback((id: string): TvMediaFocusTargets => {
    const link = links.get(id);
    const self = refs.current.get(id);
    if (!self) {
      throw new Error(`Missing TV focus ref for media item ${id}`);
    }
    return {
      self,
      left: link?.left ? refs.current.get(link.left) : undefined,
      right: link?.right ? refs.current.get(link.right) : undefined,
      up: link?.up ? refs.current.get(link.up) : undefined,
      down: link?.down ? refs.current.get(link.down) : undefined,
    };
  }, [links]);
}

import { createRef, useCallback, useMemo, useRef, useState, type RefObject } from 'react';
import type { View } from 'react-native';
import type { TvJustifiedRow, TvJustifiedTile } from './justifiedMediaRows.ts';
import { tvDebug } from '../debug.ts';

// Deterministic D-pad navigation for the native TV media walls.
//
// The screen the user sees is a JUSTIFIED grid: every row spans the content
// width, but the tiles inside it have different widths, and adjacent rows split
// that width at different places. Android's geometric focus search has no
// concept of the column the user believes they are in, so it re-derives the
// vertical target from the CURRENT tile's box on every press. Three DOWN presses
// through rows with different tile widths therefore walk sideways.
//
// The fix is to model the thing the user actually perceives: a persistent
// vertical LANE (`preferredX`, a horizontal coordinate in content-area space).
//
//   LEFT / RIGHT  the user deliberately changes horizontal position → the lane
//                 moves to the newly focused tile's centre.
//   UP / DOWN     the lane is PRESERVED; the adjacent row's tile is chosen from
//                 the lane, and landing on it must not redefine the lane.
//
// The lane is applied by pre-computing the whole "lane column" into the native
// nextFocusUp/nextFocusDown links, so a run of repeated DOWN presses needs no
// re-render at all: every link the user can reach with the current lane is
// already committed natively before the first press.

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

// Total: `Math.max(0, gap)` is NaN for a NaN gap, and a NaN gap makes every
// interval — and therefore every comparison below — NaN, which silently pins
// the vertical target to the first tile of each row.
function normalizeGap(gap: number): number {
  return Number.isFinite(gap) && gap > 0 ? gap : 0;
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

// The tile of ONE row that owns a lane.
//
//  1. a tile whose horizontal interval CONTAINS the lane wins outright — a tile
//     the lane runs through is the tile under the user's finger, however narrow
//     it is and however much the source tile overlapped a wider neighbour;
//  2. otherwise the tile whose interval is horizontally NEAREST to the lane;
//  3. centre distance is only a tie-break, and the earlier tile breaks a
//     remaining tie, so the choice is stable for identical geometry.
function laneTarget<T extends { id: string }>(
  row: readonly PositionedTile<T>[],
  lane: number,
): PositionedTile<T> | undefined {
  let best: PositionedTile<T> | undefined;
  let bestDistance = Number.POSITIVE_INFINITY;
  let bestCenterDistance = Number.POSITIVE_INFINITY;

  for (const candidate of row) {
    const distance = lane < candidate.start
      ? candidate.start - lane
      : lane > candidate.end
        ? lane - candidate.end
        : 0;
    const centerDistance = Math.abs(lane - candidate.center);
    if (
      distance < bestDistance
      || (distance === bestDistance && centerDistance < bestCenterDistance)
      || (
        distance === bestDistance
        && centerDistance === bestCenterDistance
        && candidate.tile.originalIndex < (best?.tile.originalIndex ?? Number.POSITIVE_INFINITY)
      )
    ) {
      best = candidate;
      bestDistance = distance;
      bestCenterDistance = centerDistance;
    }
  }

  return best;
}

export interface TvMediaFocusModel {
  links: Map<string, TvMediaFocusLinks>;
  // Row index of a tile, used by the lane policy to tell a horizontal move
  // (same row) from a vertical one (different row).
  rowOf: (id: string) => number | undefined;
  centerOf: (id: string) => number | undefined;
}

// Build every native focus link for the current geometry and lane.
//
// A tile gets the ACTIVE lane only when it is its row's lane tile — the only
// way to be focused on it while the lane is inherited is to have arrived
// vertically. Every other tile can only be reached horizontally or by an
// explicit restore, both of which redefine the lane as that tile's centre, so
// its links are built from its own centre. That makes the whole lane column
// consistent in a single pass: DOWN, DOWN, DOWN follows one lane, and UP walks
// back up the same one.
export function buildTvMediaFocusModel<T extends { id: string }>(
  rows: readonly TvJustifiedRow<T>[],
  gap: number,
  preferredX: number | null = null,
): TvMediaFocusModel {
  const positioned = rows.map((row) => positionRow(row, normalizeGap(gap)));
  const laneTileByRow = preferredX === null
    ? []
    : positioned.map((row) => laneTarget(row, preferredX));

  const links = new Map<string, TvMediaFocusLinks>();
  const rowIndexById = new Map<string, number>();
  const centerById = new Map<string, number>();

  positioned.forEach((row, rowIndex) => {
    row.forEach((source, tileIndex) => {
      const id = source.tile.item.id;
      rowIndexById.set(id, rowIndex);
      centerById.set(id, source.center);

      const lane = preferredX !== null && laneTileByRow[rowIndex] === source
        ? preferredX
        : source.center;

      links.set(id, {
        left: row[tileIndex - 1]?.tile.item.id,
        right: row[tileIndex + 1]?.tile.item.id,
        // Always the IMMEDIATELY adjacent row: a lane never skips a row just
        // because a farther one happens to line up better.
        up: rowIndex > 0
          ? laneTarget(positioned[rowIndex - 1], lane)?.tile.item.id
          : undefined,
        down: rowIndex < positioned.length - 1
          ? laneTarget(positioned[rowIndex + 1], lane)?.tile.item.id
          : undefined,
      });
    });
  });

  return {
    links,
    rowOf: (id) => rowIndexById.get(id),
    centerOf: (id) => centerById.get(id),
  };
}

export function buildTvMediaFocusLinks<T extends { id: string }>(
  rows: readonly TvJustifiedRow<T>[],
  gap: number,
  preferredX: number | null = null,
): Map<string, TvMediaFocusLinks> {
  return buildTvMediaFocusModel(rows, gap, preferredX).links;
}

export interface TvMediaLaneState {
  focusedId: string | null;
  preferredX: number | null;
}

export const INITIAL_TV_MEDIA_LANE: TvMediaLaneState = { focusedId: null, preferredX: null };

// 'dpad'    — the native focus engine moved focus because the user pressed a
//             direction; a row change means the lane must be preserved.
// 'restore' — the app put focus somewhere (overlay close, face-filter remap,
//             viewer close). That is an explicit new focus choice, so it
//             establishes a new lane rather than inheriting the old one.
export type TvMediaFocusArrival = 'dpad' | 'restore';

export function tvMediaLaneAfterFocus(
  model: Pick<TvMediaFocusModel, 'rowOf' | 'centerOf'>,
  state: TvMediaLaneState,
  id: string,
  arrival: TvMediaFocusArrival = 'dpad',
): TvMediaLaneState {
  const center = model.centerOf(id);
  // A tile that is not part of the current geometry (a stale focus report from
  // a row being torn down) must never move the lane.
  if (center === undefined) return state;
  // Re-focusing the tile the remote is already on changes nothing — including
  // after a menu round-trip, where restoring the SAME tile should also restore
  // the lane the user was steering, not collapse it onto that tile's centre.
  if (state.focusedId === id && state.preferredX !== null) return state;

  const row = model.rowOf(id);
  const previousRow = state.focusedId === null ? undefined : model.rowOf(state.focusedId);
  const verticalMove = arrival === 'dpad'
    && state.preferredX !== null
    && row !== undefined
    && previousRow !== undefined
    && row !== previousRow;

  return {
    focusedId: id,
    preferredX: verticalMove ? state.preferredX : center,
  };
}

export interface TvMediaGridFocus {
  // Native focus targets for one tile. Identity changes with the model, so a
  // memoized tile re-renders exactly when its links change.
  targetsFor: (id: string) => TvMediaFocusTargets;
  // A tile reported native focus — drives the persistent lane.
  onTileFocused: (id: string) => void;
  // Declare that the NEXT focus arrival is programmatic, so it defines a new
  // lane instead of inheriting the current one.
  noteFocusRestore: () => void;
}

// Ref registry shared by the native media walls. Refs stay stable when a
// FlatList row is remounted, while the link map follows the current row
// geometry after paging, filtering, a lane change or a surface-size change.
export function useTvMediaGridFocus<T extends { id: string }>(
  rows: readonly TvJustifiedRow<T>[],
  gap: number,
): TvMediaGridFocus {
  const refs = useRef(new Map<string, RefObject<View | null>>());
  for (const row of rows) {
    for (const tile of row.tiles) {
      if (!refs.current.has(tile.item.id)) {
        refs.current.set(tile.item.id, createRef<View>());
      }
    }
  }

  // ONLY the lane is render state. The focused id lives in a ref because it
  // does not change any link: a run of repeated DOWN presses keeps one lane, so
  // it re-renders nothing and every native link it needs is already committed.
  // A horizontal move does change the lane and costs one render — but LEFT and
  // RIGHT never depend on the lane, so that render can never be raced.
  const [preferredX, setPreferredX] = useState<number | null>(null);
  const laneRef = useRef<TvMediaLaneState>(INITIAL_TV_MEDIA_LANE);
  const restorePendingRef = useRef(false);

  const model = useMemo(
    () => buildTvMediaFocusModel(rows, gap, preferredX),
    [rows, gap, preferredX],
  );
  const modelRef = useRef(model);
  modelRef.current = model;

  const onTileFocused = useCallback((id: string) => {
    const arrival: TvMediaFocusArrival = restorePendingRef.current ? 'restore' : 'dpad';
    restorePendingRef.current = false;
    const previous = laneRef.current;
    const next = tvMediaLaneAfterFocus(modelRef.current, previous, id, arrival);
    if (next === previous) return;
    laneRef.current = next;
    // Positions and the lane, never an item id — see debug.ts. Row→row plus
    // kept/set is what actually diagnoses a drift on a physical remote: a lane
    // that reads `set` on a vertical move is the bug reappearing.
    tvDebug(
      'grid nav', arrival,
      'row', previous.focusedId === null ? -1 : (modelRef.current.rowOf(previous.focusedId) ?? -1),
      '->', modelRef.current.rowOf(id) ?? -1,
      'lane', Math.round(next.preferredX ?? -1),
      next.preferredX === previous.preferredX ? 'kept' : 'set',
    );
    if (next.preferredX !== previous.preferredX) setPreferredX(next.preferredX);
  }, []);

  const noteFocusRestore = useCallback(() => {
    restorePendingRef.current = true;
  }, []);

  const { links } = model;
  const targetsFor = useCallback((id: string): TvMediaFocusTargets => {
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

  return useMemo(
    () => ({ targetsFor, onTileFocused, noteFocusRestore }),
    [targetsFor, onTileFocused, noteFocusRestore],
  );
}

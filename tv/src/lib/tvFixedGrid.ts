// Deterministic fixed-column grid for the TV media walls — pure, no React.
//
// WHY THE JUSTIFIED WALL WAS REMOVED
// ----------------------------------
// The previous engine reproduced the web's JUSTIFIED wall: every row spans the
// content width, but tiles inside it have different widths and adjacent rows
// split that width at different places. Android's geometric focus search has no
// concept of the column the user believes they are in, so it re-derived the
// vertical target from the CURRENT tile's box on every press, and a run of DOWN
// presses walked sideways.
//
// That was patched with a persistent vertical LANE (`preferredX`) pushed into
// the native nextFocusUp/Down links. The lane fixed slow navigation and left a
// race that only appears at speed, which is exactly the reported defect:
//
//   press DOWN  → native focus lands on the next tile
//               → onTileFocused runs → a HORIZONTAL move sets preferredX
//               → setPreferredX re-renders → a new link map is built
//               → every tile re-renders → FocusableMediaTile copies the newly
//                 resolved neighbour Views into ITS OWN useState
//               → a second render commits the native nextFocus props
//   the next auto-repeat arrives BEFORE those two renders commit
//               → Android uses the PREVIOUS links, or an unresolved (null)
//                 target, and silently falls back to geometry
//               → geometry disagrees with the lane on a justified row
//               → fast and slow diverge.
//
// So the divergence is not a missing debounce. It is a focus graph that depends
// on React committing between two key presses. Repeat events from a held D-pad
// arrive far faster than a render pass, so any design with that dependency is
// timing-sensitive by construction.
//
// WHAT REPLACES IT
// ----------------
// A UNIFORM fixed-column grid. Two properties follow, and together they remove
// the race rather than hiding it:
//
//  1. The focus graph is a pure function of (itemCount, columns). It contains no
//     lane, no preferredX, no arrival direction and no render state, so NOTHING
//     re-renders during a navigation burst. The links committed before the first
//     press are still correct at the twentieth.
//
//  2. Because every tile has the same box and the columns line up, Android's
//     geometric fallback returns the SAME tile the explicit link names. On the
//     justified wall those two answers disagreed — that disagreement was the
//     whole bug. Here a momentarily unresolved link (a row the virtualizer has
//     not mounted yet) degrades to an equivalent answer instead of a lateral
//     drift.
//
// `additionalRenderRegions` is documented in the react-native-tvos README but is
// NOT implemented in the shipped 0.85.3-3 JavaScript (it exists in no file under
// node_modules/react-native besides that README). Point 2 is therefore what
// actually carries focus safety across a virtualization boundary, and the render
// window is sized generously on top; see PersonalLibraryScreen.

export type TvGridDirection = 'left' | 'right' | 'up' | 'down';

// Column count for a content width. Tiles are uniform, so the count is the only
// layout degree of freedom: it is chosen so a tile is wide enough to read at
// 10 feet and the row is dense enough to page a library quickly.
//
// The bounds are deliberately narrow. A 1080p Fire TV lays out at 960dp of
// content width, which lands on 5 columns; a 720p panel and the smaller
// effective viewports land on 4. This mirrors the target in the task brief and
// is asserted by tests at the real device widths rather than being tuned by
// eye.
export const TV_GRID_MIN_COLUMNS = 3;
export const TV_GRID_MAX_COLUMNS = 6;
// Target tile width in dp. 960dp content / 180dp ≈ 5 columns.
const TARGET_TILE_WIDTH = 180;

export function tvGridColumns(contentWidth: number): number {
  if (!Number.isFinite(contentWidth) || contentWidth <= 0) return TV_GRID_MIN_COLUMNS;
  const raw = Math.round(contentWidth / TARGET_TILE_WIDTH);
  return Math.min(TV_GRID_MAX_COLUMNS, Math.max(TV_GRID_MIN_COLUMNS, raw));
}

// Tile box for a fixed-column row. Uniform by construction: every tile in the
// grid gets the same width and height, which is what makes the geometric
// fallback agree with the explicit graph.
export function tvGridTileWidth(contentWidth: number, columns: number, gap: number): number {
  const totalGap = gap * Math.max(0, columns - 1);
  return Math.max(1, Math.floor((contentWidth - totalGap) / columns));
}

// THE FOCUS GRAPH.
//
// LEFT  → previous item in the SAME row (never wraps to the previous row: a
//         wrap makes a held LEFT walk the whole library backwards, which is
//         never what the user means).
// RIGHT → next item in the same row.
// UP    → same column, previous row.
// DOWN  → same column, next row; when that row is INCOMPLETE and has no cell in
//         this column, the deterministic fallback is its LAST item — the nearest
//         column that exists. This is the only special case, it depends on
//         nothing but the counts, and it is symmetric with UP (coming back up
//         from a short last row lands on that item's own column).
//
// Returns null when the move leaves the grid, so the caller can decide (stay
// put, hand focus to chrome). Never returns an out-of-range index.
export function tvGridNeighbor(
  index: number,
  direction: TvGridDirection,
  count: number,
  columns: number,
): number | null {
  if (!Number.isInteger(index) || index < 0 || index >= count) return null;
  if (columns < 1) return null;

  const row = Math.floor(index / columns);
  const column = index % columns;
  const lastRow = Math.floor((count - 1) / columns);

  switch (direction) {
    case 'left':
      return column > 0 ? index - 1 : null;
    case 'right':
      return column < columns - 1 && index + 1 < count ? index + 1 : null;
    case 'up': {
      if (row === 0) return null;
      return (row - 1) * columns + column;
    }
    case 'down': {
      if (row >= lastRow) return null;
      const target = (row + 1) * columns + column;
      // Incomplete last row: fall back to its final item (nearest column).
      return target < count ? target : count - 1;
    }
  }
}

// Apply a whole sequence of directions. This is the function the burst tests
// drive: it is the same code path a single tap and a twenty-press auto-repeat
// take, because there is no timing input to differ on. A move that leaves the
// grid leaves the index unchanged.
export function tvGridWalk(
  start: number,
  directions: readonly TvGridDirection[],
  count: number,
  columns: number,
): number {
  return directions.reduce<number>(
    (index, direction) => tvGridNeighbor(index, direction, count, columns) ?? index,
    start,
  );
}

export interface TvGridRow<T> {
  key: string;
  // Index of the first item of this row in the flat item list, so a renderer
  // can label tiles without recomputing it.
  firstIndex: number;
  items: readonly T[];
}

// Chunk a flat item list into fixed-column rows for a virtualized list. The row
// key is the first item's id: stable across pagination (appending items never
// renumbers an existing row) and unique.
export function buildTvGridRows<T>(
  items: readonly T[],
  columns: number,
  getId: (item: T) => string,
): TvGridRow<T>[] {
  if (columns < 1) return [];
  const rows: TvGridRow<T>[] = [];
  for (let start = 0; start < items.length; start += columns) {
    const slice = items.slice(start, start + columns);
    rows.push({ key: getId(slice[0]), firstIndex: start, items: slice });
  }
  return rows;
}

// Row index a flat item index lives in — used to scroll the focused row into
// view without the caller re-deriving the layout.
export function tvGridRowOf(index: number, columns: number): number {
  return columns < 1 ? 0 : Math.floor(index / columns);
}

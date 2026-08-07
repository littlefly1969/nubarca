import assert from 'node:assert/strict';
import test from 'node:test';
import {
  INITIAL_TV_MEDIA_LANE,
  buildTvMediaFocusLinks,
  buildTvMediaFocusModel,
  tvMediaLaneAfterFocus,
  type TvMediaLaneState,
} from './mediaGridFocus.ts';
import type { TvJustifiedRow } from './justifiedMediaRows.ts';

interface Item {
  id: string;
}

const GAP = 4;

function row(ids: string[], widths: number[], startIndex: number): TvJustifiedRow<Item> {
  return {
    key: `row-${ids[0]}`,
    height: 100,
    isLast: false,
    tiles: ids.map((id, index) => ({
      item: { id },
      originalIndex: startIndex + index,
      width: widths[index],
      height: 100,
    })),
  };
}

// The horizontal interval every tile occupies, mirroring the module's own
// positioning, so a test can assert "the lane is still inside the tile the
// remote landed on" rather than hard-coding an id and hoping.
function intervals(
  rows: readonly TvJustifiedRow<Item>[],
  gap: number,
): Map<string, { start: number; end: number; center: number }> {
  const out = new Map<string, { start: number; end: number; center: number }>();
  for (const r of rows) {
    let x = 0;
    for (const tile of r.tiles) {
      const start = x;
      const end = start + tile.width;
      x = end + Math.max(0, gap);
      out.set(tile.item.id, { start, end, center: (start + end) / 2 });
    }
  }
  return out;
}

type Press = 'up' | 'down' | 'left' | 'right';

// Drives the SHIPPED sequencing: the link map is rebuilt from the committed
// lane, a press follows the link that is already there, and the lane policy
// then runs on arrival. A test therefore exercises exactly what the remote
// exercises — not a convenience path that only exists in the test.
function walk(
  rows: readonly TvJustifiedRow<Item>[],
  gap: number,
  startId: string,
  presses: readonly Press[],
): { path: string[]; lane: TvMediaLaneState } {
  let model = buildTvMediaFocusModel(rows, gap, null);
  let lane = tvMediaLaneAfterFocus(model, INITIAL_TV_MEDIA_LANE, startId, 'restore');
  model = buildTvMediaFocusModel(rows, gap, lane.preferredX);

  const path = [startId];
  for (const press of presses) {
    const next = lane.focusedId === null ? undefined : model.links.get(lane.focusedId)?.[press];
    if (next === undefined) {
      path.push('(blocked)');
      continue;
    }
    lane = tvMediaLaneAfterFocus(model, lane, next, 'dpad');
    model = buildTvMediaFocusModel(rows, gap, lane.preferredX);
    path.push(next);
  }
  return { path, lane };
}

// Rows whose tiles split the same 1200px content width at very different
// places — the geometry that made Android's per-tile focus search walk
// sideways.
const UNEVEN_ROWS: TvJustifiedRow<Item>[] = [
  row(['x0', 'x1'], [200, 996], 0),
  row(['a', 'b'], [760, 436], 2),
  row(['c', 'd', 'e'], [300, 596, 296], 4),
];

test('the lane, not the source tile box, chooses the vertical target', () => {
  // x0 spans [0,200] (centre 100) — a in the next row spans [0,760] and d spans
  // [304,900]. Choosing by maximum overlap with the SOURCE box sends x0→a→d,
  // which is a 500px walk to the right for two presses of DOWN. The lane stays
  // at 100 and lands on the tile that actually contains it.
  const { path, lane } = walk(UNEVEN_ROWS, GAP, 'x0', ['down', 'down']);
  assert.deepEqual(path, ['x0', 'a', 'c']);
  assert.equal(lane.preferredX, 100);
});

test('a tile containing the lane wins however wide the source tile is', () => {
  // Focused on `a`, which is 760px wide, with the lane at 100. `d` overlaps
  // `a` far more than `c` does; `c` is the tile the lane runs through.
  const links = buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, 100);
  assert.equal(links.get('a')?.down, 'c');

  // Same geometry, lane on the other side: the wide tile is not privileged.
  assert.equal(buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, 1000).get('b')?.down, 'e');
});

test('repeated DOWN through radically different rows never drifts sideways', () => {
  const rows = [
    row(['panorama'], [1200], 0),
    row(['sliver', 'wide'], [40, 1156], 1),
    row(['k0', 'k1', 'k2'], [400, 396, 396], 3),
    row(['m0', 'm1'], [596, 600], 6),
  ];
  const box = intervals(rows, GAP);

  const { path, lane } = walk(rows, GAP, 'panorama', ['down', 'down', 'down']);
  assert.deepEqual(path, ['panorama', 'wide', 'k1', 'm1']);
  // The lane never moved …
  assert.equal(lane.preferredX, 600);
  // … and every tile the remote landed on still contains it, which is what
  // "the visual column is preserved" means on screen.
  for (const id of path) {
    const { start, end } = box.get(id)!;
    assert.ok(start <= 600 && 600 <= end, `${id} lost the lane`);
  }
});

test('UP retraces the same lane back to the tile DOWN came from', () => {
  const { path, lane } = walk(UNEVEN_ROWS, GAP, 'x0', ['down', 'down', 'up', 'up']);
  assert.deepEqual(path, ['x0', 'a', 'c', 'a', 'x0']);
  assert.equal(lane.preferredX, 100);
});

test('a horizontal move re-establishes the lane and the next DOWN follows it', () => {
  // DOWN keeps the lane at 100 (x0's centre); RIGHT is a deliberate horizontal
  // choice, so the lane becomes b's centre (982) and the next DOWN goes to the
  // tile under THAT, not back under x0.
  const { path, lane } = walk(UNEVEN_ROWS, GAP, 'x0', ['down', 'right', 'down']);
  assert.deepEqual(path, ['x0', 'a', 'b', 'e']);
  assert.equal(lane.preferredX, 982);
});

test('a lane in a gap picks the nearest interval, centre distance only breaks ties', () => {
  // c ends at 300 and d starts at 304.
  assert.equal(buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, 301).get('a')?.down, 'c');
  assert.equal(buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, 303).get('a')?.down, 'd');
  // Exactly halfway: identical interval distance, so the nearer CENTRE wins.
  assert.equal(buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, 302).get('a')?.down, 'c');
});

test('a fully symmetric tie resolves to the earlier tile, never at random', () => {
  const rows = [
    row(['top'], [300], 0),
    // Both tiles are 50px from the lane at 150 AND 100px from it by centre.
    row(['near', 'far'], [100, 100], 1),
  ];
  const links = buildTvMediaFocusLinks(rows, 100, 150);
  assert.equal(links.get('top')?.down, 'near');
  // Deterministic under repetition, and independent of build order.
  assert.equal(buildTvMediaFocusLinks(rows, 100, 150).get('top')?.down, 'near');
});

test('vertical focus never skips the immediately adjacent row', () => {
  const rowIds = [
    new Set(['x0', 'x1']),
    new Set(['a', 'b']),
    new Set(['c', 'd', 'e']),
  ];
  // Across a sweep of lanes, including ones outside the content width.
  for (const lane of [null, -500, 0, 100, 302, 601, 982, 1199, 4000]) {
    const links = buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, lane);
    for (const [rowIndex, ids] of rowIds.entries()) {
      for (const id of ids) {
        const link = links.get(id);
        if (rowIndex > 0) assert.ok(rowIds[rowIndex - 1].has(link?.up ?? ''), `up ${id} lane ${lane}`);
        else assert.equal(link?.up, undefined);
        if (rowIndex < rowIds.length - 1) {
          assert.ok(rowIds[rowIndex + 1].has(link?.down ?? ''), `down ${id} lane ${lane}`);
        } else assert.equal(link?.down, undefined);
      }
    }
  }
});

test('horizontal navigation stays inside its row and does not wrap', () => {
  const links = buildTvMediaFocusLinks(UNEVEN_ROWS, GAP, 100);
  assert.equal(links.get('c')?.right, 'd');
  assert.equal(links.get('d')?.left, 'c');
  assert.equal(links.get('d')?.right, 'e');
  // Row edges: no wrap-around, no diagonal escape.
  assert.equal(links.get('c')?.left, undefined);
  assert.equal(links.get('e')?.right, undefined);
});

test('a single-tile row absorbs every lane in both directions', () => {
  const rows = [
    row(['top', 'top2'], [1000, 196], 0),
    row(['only'], [1200], 2),
    row(['low', 'low2'], [196, 1000], 3),
  ];
  for (const lane of [0, 500, 1200]) {
    const links = buildTvMediaFocusLinks(rows, GAP, lane);
    assert.equal(links.get('top')?.down, 'only');
    assert.equal(links.get('top2')?.down, 'only');
    assert.equal(links.get('low')?.up, 'only');
    assert.equal(links.get('low2')?.up, 'only');
  }
});

test('a very narrow portrait tile is reachable and keeps its own lane', () => {
  const rows = [
    row(['panorama'], [1200], 0),
    row(['sliver', 'wide'], [40, 1156], 1),
    row(['k0', 'k1', 'k2'], [400, 396, 396], 3),
  ];
  // Landing on the 40px sliver by pressing LEFT sets the lane to its centre
  // (20), and the following DOWN stays at the far left instead of snapping
  // back under the wide tile.
  const { path, lane } = walk(rows, GAP, 'panorama', ['down', 'left', 'down']);
  assert.deepEqual(path, ['panorama', 'wide', 'sliver', 'k0']);
  assert.equal(lane.preferredX, 20);
});

test('an incomplete last row that does not span the width keeps the nearest tile', () => {
  const rows = [
    row(['full0', 'full1'], [600, 596], 0),
    // Left-aligned leftovers: nothing occupies the right half of the surface.
    row(['tail0', 'tail1'], [200, 200], 2),
  ];
  const links = buildTvMediaFocusLinks(rows, GAP, 950);
  assert.equal(links.get('full1')?.down, 'tail1');
  assert.equal(buildTvMediaFocusLinks(rows, GAP, 100).get('full0')?.down, 'tail0');
});

test('a zero or negative gap is normalized rather than shifting the geometry', () => {
  const rows = [
    row(['p0', 'p1'], [600, 600], 0),
    row(['q0', 'q1', 'q2'], [400, 400, 400], 2),
  ];
  const zero = buildTvMediaFocusLinks(rows, 0, 500);
  assert.deepEqual(buildTvMediaFocusLinks(rows, -8, 500), zero);
  assert.deepEqual(buildTvMediaFocusLinks(rows, Number.NaN, 500), zero);
  assert.equal(zero.get('p0')?.down, 'q1');
});

test('the lane policy keeps the lane vertically and rebuilds it horizontally', () => {
  const model = buildTvMediaFocusModel(UNEVEN_ROWS, GAP, null);

  const start = tvMediaLaneAfterFocus(model, INITIAL_TV_MEDIA_LANE, 'x0', 'restore');
  assert.equal(start.preferredX, 100);

  // Vertical arrival inherits the lane even though `a`'s centre is 380.
  const down = tvMediaLaneAfterFocus(model, start, 'a', 'dpad');
  assert.equal(down.preferredX, 100);

  // Horizontal arrival is a deliberate choice: the lane becomes b's centre.
  const right = tvMediaLaneAfterFocus(model, down, 'b', 'dpad');
  assert.equal(right.preferredX, 982);

  // A programmatic restore is an explicit new focus choice, never an inherited
  // lane — the app moved focus, the user did not steer there.
  const restored = tvMediaLaneAfterFocus(model, right, 'c', 'restore');
  assert.equal(restored.preferredX, 150);

  // A focus report for a tile that is no longer part of the geometry (a row
  // being torn down) must not move the lane at all.
  assert.equal(tvMediaLaneAfterFocus(model, restored, 'gone', 'dpad'), restored);
});

test('the lane survives a live append that rebuilds the rows', () => {
  const before = buildTvMediaFocusModel(UNEVEN_ROWS, GAP, 100);
  const lane = tvMediaLaneAfterFocus(before, INITIAL_TV_MEDIA_LANE, 'a', 'dpad');
  assert.equal(lane.preferredX, 380);

  // A Party upload appends a row; the same lane still selects by containment.
  const grown = [...UNEVEN_ROWS, row(['n0', 'n1'], [700, 496], 7)];
  const after = buildTvMediaFocusLinks(grown, GAP, 100);
  assert.equal(after.get('c')?.down, 'n0');
  assert.equal(after.get('a')?.down, 'c');
});

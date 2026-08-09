import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  buildTvGridRows,
  TV_GRID_MAX_COLUMNS,
  TV_GRID_MIN_COLUMNS,
  tvGridColumns,
  tvGridNeighbor,
  tvGridRowOf,
  tvGridTileWidth,
  tvGridWalk,
  type TvGridDirection,
} from './tvFixedGrid.ts';

// A 5-column grid of 13 items: rows [0..4] [5..9] [10..12]. The last row is
// deliberately incomplete so every test below exercises the one special case.
const COLUMNS = 5;
const COUNT = 13;

const dir = (s: string): TvGridDirection[] =>
  s.split(' ').filter(Boolean).map((token) => {
    switch (token) {
      case 'U': return 'up';
      case 'D': return 'down';
      case 'L': return 'left';
      case 'R': return 'right';
      default: throw new Error(`bad direction ${token}`);
    }
  });

// ---------------------------------------------------------------- columns

test('column count lands on the intended density at real TV widths', () => {
  // 1080p Fire TV lays out at 960dp; minus the ~34dp overscan inset per side.
  assert.equal(tvGridColumns(960 - 68), 5);
  // 720p panels lay out narrower.
  assert.equal(tvGridColumns(720 - 50), 4);
});

test('column count is clamped, never zero or absurd', () => {
  assert.equal(tvGridColumns(0), TV_GRID_MIN_COLUMNS);
  assert.equal(tvGridColumns(Number.NaN), TV_GRID_MIN_COLUMNS);
  assert.equal(tvGridColumns(-100), TV_GRID_MIN_COLUMNS);
  assert.equal(tvGridColumns(10_000), TV_GRID_MAX_COLUMNS);
});

test('tiles are uniform and fit the content width with the gaps', () => {
  const width = tvGridTileWidth(900, 5, 4);
  assert.equal(width, Math.floor((900 - 4 * 4) / 5));
  assert.ok(width * 5 + 4 * 4 <= 900, 'a row must never exceed the content width');
});

// ------------------------------------------------------------ basic moves

test('left and right move within the row and never wrap', () => {
  assert.equal(tvGridNeighbor(2, 'right', COUNT, COLUMNS), 3);
  assert.equal(tvGridNeighbor(2, 'left', COUNT, COLUMNS), 1);
  // Row edges: a wrap would make a held LEFT walk the library backwards.
  assert.equal(tvGridNeighbor(0, 'left', COUNT, COLUMNS), null);
  assert.equal(tvGridNeighbor(4, 'right', COUNT, COLUMNS), null);
  assert.equal(tvGridNeighbor(5, 'left', COUNT, COLUMNS), null);
});

test('up and down keep the column', () => {
  assert.equal(tvGridNeighbor(7, 'up', COUNT, COLUMNS), 2);
  assert.equal(tvGridNeighbor(2, 'down', COUNT, COLUMNS), 7);
});

test('first row has no up, last row has no down', () => {
  assert.equal(tvGridNeighbor(3, 'up', COUNT, COLUMNS), null);
  assert.equal(tvGridNeighbor(11, 'down', COUNT, COLUMNS), null);
});

test('down into an incomplete last row lands on its nearest existing column', () => {
  // Row 2 holds indices 10,11,12 only. Columns 3 and 4 do not exist there.
  assert.equal(tvGridNeighbor(8, 'down', COUNT, COLUMNS), 12);
  assert.equal(tvGridNeighbor(9, 'down', COUNT, COLUMNS), 12);
  // Columns that DO exist are unaffected.
  assert.equal(tvGridNeighbor(5, 'down', COUNT, COLUMNS), 10);
  assert.equal(tvGridNeighbor(7, 'down', COUNT, COLUMNS), 12);
});

test('up from the incomplete last row returns to that item own column', () => {
  assert.equal(tvGridNeighbor(12, 'up', COUNT, COLUMNS), 7);
  assert.equal(tvGridNeighbor(10, 'up', COUNT, COLUMNS), 5);
});

test('a right at the end of a partially filled row stops', () => {
  assert.equal(tvGridNeighbor(12, 'right', COUNT, COLUMNS), null);
});

test('out-of-range and degenerate inputs return null, never a bad index', () => {
  assert.equal(tvGridNeighbor(-1, 'down', COUNT, COLUMNS), null);
  assert.equal(tvGridNeighbor(COUNT, 'down', COUNT, COLUMNS), null);
  assert.equal(tvGridNeighbor(1.5, 'down', COUNT, COLUMNS), null);
  assert.equal(tvGridNeighbor(0, 'down', COUNT, 0), null);
});

// ------------------------------------------------- the reported defect

test('the burst from the acceptance plan is deterministic', () => {
  // DOWN x20, UP x12, RIGHT x3, DOWN x10, LEFT x2 over a large grid.
  const count = 500;
  const columns = 5;
  const sequence: TvGridDirection[] = [
    ...Array<TvGridDirection>(20).fill('down'),
    ...Array<TvGridDirection>(12).fill('up'),
    ...Array<TvGridDirection>(3).fill('right'),
    ...Array<TvGridDirection>(10).fill('down'),
    ...Array<TvGridDirection>(2).fill('left'),
  ];
  // 0 →(20 down) row20 col0 = 100 →(12 up) row8 col0 = 40 →(3 right) 43
  // →(10 down) row18 col3 = 93 →(2 left) 91.
  assert.equal(tvGridWalk(0, sequence, count, columns), 91);
});

test('a rapid burst and the same presses entered slowly land on the same tile', () => {
  // This is the reported physical defect, expressed as a property. The engine
  // takes NO timing input: `tvGridWalk` folding the whole sequence at once is
  // the "held D-pad" case, and stepping one press at a time — with arbitrary
  // pauses, re-reads and intervening no-op renders in between — is the "slow
  // taps" case. They are required to agree for every sequence.
  const count = 137;
  const columns = 5;
  const sequences = [
    'D D D D D D D D D D D D D D D D D D D D U U U U U U U U U U U U R R R D D D D D D D D D D L L',
    'D R D R D R D R U L U L D D D R R R U U U L L L D D D',
    'R R R R R R R R D D L L L L U U U D D D D R R',
    'U U U L L L D D D R R R U U D D',
    'D D D D D D D D D D D D D D D D D D D D D D D D D D D D D D',
  ];
  for (const source of sequences) {
    const directions = dir(source);
    const fast = tvGridWalk(0, directions, count, columns);
    let slow = 0;
    for (const direction of directions) {
      // One press, then "time passes" — nothing about the engine can observe it.
      slow = tvGridNeighbor(slow, direction, count, columns) ?? slow;
    }
    assert.equal(fast, slow, `rapid and slow must agree for: ${source}`);
  }
});

test('a held direction never drifts sideways', () => {
  // The justified wall's failure mode: repeated DOWN presses changing column.
  const count = 200;
  const columns = 5;
  for (let start = 0; start < columns; start++) {
    let index = start;
    for (let i = 0; i < 30; i++) {
      const next = tvGridNeighbor(index, 'down', count, columns);
      if (next === null) break;
      index = next;
      // 200 items over 5 columns is a complete grid, so the column is invariant.
      assert.equal(index % columns, start, 'a DOWN burst must hold its column');
    }
  }
});

test('down then up returns to the starting tile on a complete grid', () => {
  const count = 100;
  const columns = 5;
  for (let index = 0; index < count - columns; index++) {
    const down = tvGridNeighbor(index, 'down', count, columns);
    assert.notEqual(down, null);
    assert.equal(tvGridNeighbor(down!, 'up', count, columns), index);
  }
});

test('no walk can ever escape the grid', () => {
  const count = 23;
  const columns = 4;
  const directions: TvGridDirection[] = ['up', 'down', 'left', 'right'];
  for (let start = 0; start < count; start++) {
    let index = start;
    // A long pseudo-random-but-deterministic walk.
    for (let step = 0; step < 400; step++) {
      const direction = directions[(step * 7 + start * 3) % 4];
      index = tvGridNeighbor(index, direction, count, columns) ?? index;
      assert.ok(Number.isInteger(index) && index >= 0 && index < count,
        `walk escaped the grid at step ${step}: ${index}`);
    }
  }
});

// ------------------------------------------------------------------ rows

test('rows chunk the item list and carry their first flat index', () => {
  const items = Array.from({ length: COUNT }, (_, i) => ({ id: `i${i}` }));
  const rows = buildTvGridRows(items, COLUMNS, (item) => item.id);
  assert.equal(rows.length, 3);
  assert.deepEqual(rows.map((r) => r.firstIndex), [0, 5, 10]);
  assert.deepEqual(rows.map((r) => r.items.length), [5, 5, 3]);
  assert.deepEqual(rows.map((r) => r.key), ['i0', 'i5', 'i10']);
});

test('appending a page keeps every existing row key stable', () => {
  const first = Array.from({ length: 10 }, (_, i) => ({ id: `i${i}` }));
  const appended = [...first, ...Array.from({ length: 7 }, (_, i) => ({ id: `j${i}` }))];
  const before = buildTvGridRows(first, COLUMNS, (x) => x.id).map((r) => r.key);
  const after = buildTvGridRows(appended, COLUMNS, (x) => x.id).map((r) => r.key);
  assert.deepEqual(after.slice(0, before.length), before,
    'pagination must not renumber mounted rows (that would remount them and drop focus)');
});

test('the row of an index matches the chunking', () => {
  assert.equal(tvGridRowOf(0, COLUMNS), 0);
  assert.equal(tvGridRowOf(4, COLUMNS), 0);
  assert.equal(tvGridRowOf(5, COLUMNS), 1);
  assert.equal(tvGridRowOf(12, COLUMNS), 2);
});

test('an empty grid produces no rows and no moves', () => {
  assert.deepEqual(buildTvGridRows([], COLUMNS, () => 'x'), []);
  assert.equal(tvGridNeighbor(0, 'down', 0, COLUMNS), null);
});

test('a single-item grid is inert in every direction', () => {
  for (const direction of ['up', 'down', 'left', 'right'] as TvGridDirection[]) {
    assert.equal(tvGridNeighbor(0, direction, 1, COLUMNS), null);
  }
});

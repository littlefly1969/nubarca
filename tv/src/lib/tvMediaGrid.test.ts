import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildTvMediaGridModel,
  buildTvMediaGridRows,
  TV_MEDIA_GRID_GAP,
  type TvMediaGridRow,
} from './tvMediaGrid.ts';

interface Item { id: string; ratio: number }

const build = (items: Item[], width = 1200, height = 200) => buildTvMediaGridRows({
  items,
  contentWidth: width,
  targetRowHeight: height,
  getAspectRatio: (item) => item.ratio,
  getId: (item) => item.id,
});

test('full rows fill the surface while every tile keeps its aspect ratio', () => {
  const rows = build(Array.from({ length: 18 }, (_, index) => ({
    id: `m${index}`,
    ratio: index % 3 === 0 ? 0.75 : 1.6,
  })));
  for (const row of rows) {
    for (const tile of row.tiles) {
      assert.equal(tile.height, row.height);
      assert.ok(tile.width >= 1);
    }
    if (!row.isLast) {
      assert.equal(
        row.tiles.reduce((sum, tile) => sum + tile.width, 0)
          + TV_MEDIA_GRID_GAP * (row.tiles.length - 1),
        1200,
      );
    }
  }
  assert.ok(rows.some((row) => new Set(row.tiles.map((tile) => tile.width)).size > 1));
});

test('an incomplete last row stays left aligned and row identity includes its contents', () => {
  const first = build([{ id: 'a', ratio: 1 }]);
  const second = build([{ id: 'a', ratio: 1 }, { id: 'b', ratio: 1 }]);
  assert.equal(first[0].tiles[0].height, 200);
  assert.ok(first[0].tiles[0].width < 1200);
  assert.notEqual(first[0].key, second[0].key);
});

test('invalid dimensions degrade to positive square geometry', () => {
  const rows = build([
    { id: 'nan', ratio: Number.NaN },
    { id: 'zero', ratio: 0 },
    { id: 'negative', ratio: -2 },
  ]);
  for (const tile of rows.flatMap((row) => row.tiles)) {
    assert.ok(tile.width > 0);
    assert.ok(tile.height > 0);
  }
});

function row(ids: string[], widths: number[], start: number): TvMediaGridRow<Item> {
  return {
    key: ids.join('|'),
    height: 100,
    isLast: false,
    tiles: ids.map((id, index) => ({
      item: { id, ratio: widths[index] / 100 },
      originalIndex: start + index,
      width: widths[index],
      height: 100,
    })),
  };
}

test('vertical links are static, adjacent-row and deterministic under a burst', () => {
  const rows = [
    row(['a', 'b'], [300, 896], 0),
    row(['c', 'd', 'e'], [250, 500, 442], 2),
    row(['f', 'g'], [700, 496], 5),
  ];
  const links = buildTvMediaGridModel(rows, (item) => item.id).links;
  const walk = (start: string, directions: Array<'up' | 'down'>) => directions.reduce(
    (id, direction) => links.get(id)?.[direction] ?? id,
    start,
  );
  assert.equal(links.get('a')?.down, 'c');
  assert.equal(links.get('c')?.down, 'f');
  assert.equal(walk('a', ['down', 'down']), 'f');
  assert.equal(walk('a', ['down', 'down']), 'f');
  assert.ok(new Set(['c', 'd', 'e']).has(links.get('a')?.down ?? ''));
  assert.ok(new Set(['f', 'g']).has(links.get('c')?.down ?? ''));
});

test('horizontal links never wrap into another row', () => {
  const rows = [row(['a', 'b'], [500, 696], 0), row(['c'], [1200], 2)];
  const links = buildTvMediaGridModel(rows, (item) => item.id).links;
  assert.equal(links.get('a')?.left, undefined);
  assert.equal(links.get('a')?.right, 'b');
  assert.equal(links.get('b')?.right, undefined);
  assert.equal(links.get('c')?.left, undefined);
});

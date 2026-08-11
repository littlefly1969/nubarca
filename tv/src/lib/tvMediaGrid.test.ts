import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildTvMediaGridRows,
  TV_MEDIA_GRID_GAP,
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

test('appending an item that completes the last row preserves its identity', () => {
  const incomplete = Array.from({ length: 5 }, (_, index) => ({
    id: String.fromCharCode('a'.charCodeAt(0) + index),
    ratio: 1,
  }));
  const first = build(incomplete);
  const second = build([...incomplete, { id: 'f', ratio: 1 }]);
  assert.equal(first[0].tiles.length, 5);
  assert.equal(second[0].tiles.length, 6);
  assert.equal(first[0].key, second[0].key);
  assert.deepEqual(
    second[0].tiles.slice(0, incomplete.length).map((tile) => tile.item.id),
    incomplete.map((item) => item.id),
  );
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

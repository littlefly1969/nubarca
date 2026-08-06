import assert from 'node:assert/strict';
import test from 'node:test';
import { buildTvJustifiedRows } from './justifiedMediaRows.ts';

interface Fixture { id: string; ar: number; }

function build(items: Fixture[], contentWidth = 1200, targetRowHeight = 200, gap = 6) {
  return buildTvJustifiedRows({
    items,
    contentWidth,
    targetRowHeight,
    gap,
    getAspectRatio: (it) => it.ar,
    getId: (it) => it.id,
  });
}

test('empty input yields no rows', () => {
  assert.deepEqual(build([]), []);
});

test('full rows span the content width exactly (widths + gaps === contentWidth)', () => {
  const items: Fixture[] = Array.from({ length: 12 }, (_v, i) => ({ id: `p${i}`, ar: 1.5 }));
  const rows = build(items, 1200, 200, 6);
  // Every row except possibly the last is justified to the content width.
  for (const row of rows) {
    if (row.isLast) continue;
    const total = row.tiles.reduce((a, t) => a + t.width, 0) + 6 * (row.tiles.length - 1);
    assert.equal(total, 1200);
  }
});

test('tiles in a row share one height, widths follow aspect ratio', () => {
  const rows = build([
    { id: 'a', ar: 2 }, { id: 'b', ar: 0.5 }, { id: 'c', ar: 1 },
    { id: 'd', ar: 1 }, { id: 'e', ar: 1 }, { id: 'f', ar: 1 },
  ]);
  for (const row of rows) {
    const h = row.tiles[0].height;
    for (const tile of row.tiles) {
      assert.equal(tile.height, h);
      assert.ok(tile.width >= 1 && tile.height >= 1);
    }
    // A wider aspect ratio → wider tile at the same height.
    const wide = row.tiles.find((t) => t.item.id === 'a');
    const tall = row.tiles.find((t) => t.item.id === 'b');
    if (wide && tall) assert.ok(wide.width > tall.width);
  }
});

test('the last row is NOT stretched when it is clearly incomplete', () => {
  // One very wide-ratio tile can never fill a 1200px row at a sane height, so
  // the incomplete last row is left-aligned at target height, not stretched.
  const rows = build([{ id: 'solo', ar: 1 }], 1200, 200, 6);
  assert.equal(rows.length, 1);
  const only = rows[0];
  assert.ok(only.isLast);
  const total = only.tiles.reduce((a, t) => a + t.width, 0);
  assert.ok(total < 1200, `last row should not fill width, got ${total}`);
  assert.equal(only.tiles[0].height, 200);
});

test('keys use the item id, never a bare index', () => {
  const rows = build(Array.from({ length: 8 }, (_v, i) => ({ id: `x${i}`, ar: 1.3 })));
  for (const row of rows) {
    assert.ok(row.key.includes(row.tiles[0].item.id));
  }
});

test('originalIndex maps a tile back to the source order', () => {
  const items = Array.from({ length: 9 }, (_v, i) => ({ id: `i${i}`, ar: 1 }));
  const rows = build(items);
  const flat = rows.flatMap((r) => r.tiles);
  flat.forEach((tile, i) => {
    assert.equal(tile.originalIndex, i);
    assert.equal(tile.item.id, `i${i}`);
  });
});

test('invalid aspect ratios degrade to a safe square (no zero/negative sizes)', () => {
  const rows = build([
    { id: 'a', ar: Number.NaN }, { id: 'b', ar: -3 }, { id: 'c', ar: 0 },
  ]);
  for (const tile of rows.flatMap((r) => r.tiles)) {
    assert.ok(tile.width >= 1 && tile.height >= 1);
  }
});

test('a tighter visual gap preserves row density and gives the width back to previews', () => {
  const items = Array.from({ length: 18 }, (_v, i) => ({ id: `m${i}`, ar: 1 }));
  const former = buildTvJustifiedRows({
    items,
    contentWidth: 960,
    targetRowHeight: 150,
    gap: 12,
    getAspectRatio: (it) => it.ar,
    getId: (it) => it.id,
  });
  const compact = buildTvJustifiedRows({
    items,
    contentWidth: 960,
    targetRowHeight: 150,
    gap: 4,
    packingGap: 12,
    getAspectRatio: (it) => it.ar,
    getId: (it) => it.id,
  });

  assert.deepEqual(
    compact.map((row) => row.tiles.map((tile) => tile.item.id)),
    former.map((row) => row.tiles.map((tile) => tile.item.id)),
  );
  assert.ok(compact[0].tiles[0].width > former[0].tiles[0].width);
  assert.equal(
    compact[0].tiles.reduce((sum, tile) => sum + tile.width, 0)
      + 4 * (compact[0].tiles.length - 1),
    960,
  );
});

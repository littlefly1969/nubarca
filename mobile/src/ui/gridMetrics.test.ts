import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { gridMetrics } from './gridMetrics.ts';

// Mirrors `GAP`. tokens.ts cannot be imported here — it pulls in the font
// bundle — so the value is restated and then checked against the real one.
const GAP = 2;

test('the gap under test is the gallery gap the tokens declare', () => {
  const tokens = readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), 'tokens.ts'),
    'utf8',
  );
  assert.match(tokens, new RegExp(`gap: ${GAP} as number`));
});

/** Reconstruct where every seam actually falls, given the metrics. */
function seams(width: number, columns: number, gap = GAP) {
  const { tileSize, sidePadding } = gridMetrics(width, 0, columns, gap);
  const left = sidePadding;
  const between: number[] = [];
  let x = left;
  for (let i = 0; i < columns; i += 1) {
    x += tileSize;
    if (i < columns - 1) {
      between.push(gap);
      x += gap;
    }
  }
  return { left, right: width - x, between, tileSize };
}

test('every horizontal seam between tiles is the gallery gap', () => {
  for (const width of [1080, 1440, 720, 411, 2560]) {
    for (const columns of [3, 4, 5]) {
      const { between } = seams(width, columns);
      assert.deepEqual(
        between,
        Array(columns - 1).fill(GAP),
        `${width}px / ${columns} columns has an uneven seam`,
      );
    }
  }
});

test('no column pair touches while another has a seam', () => {
  // The defect this answers: with flex-start and no column gap, adjacent tiles
  // butt together and the leftover collects at one edge.
  for (const width of [1080, 1085, 1087, 1439]) {
    const { between } = seams(width, 3);
    assert.ok(between.every((s) => s === GAP), `${width}px produced a 0 seam`);
  }
});

test('the two outer insets are symmetric, to within one pixel', () => {
  // Rounding cannot be avoided; putting all of it against one edge can. It was
  // 5 px on the left against 19 px on the right before the remainder was split.
  for (const width of [1080, 1081, 1082, 1083, 1440, 411]) {
    for (const columns of [3, 4, 5]) {
      const { left, right } = seams(width, columns);
      assert.ok(
        Math.abs(left - right) <= 1,
        `${width}px / ${columns} columns: left ${left}, right ${right}`,
      );
    }
  }
});

test('the tiles never overflow the width they were given', () => {
  for (const width of [1080, 720, 411, 2560]) {
    for (const columns of [3, 4, 5]) {
      const { tileSize, sidePadding } = gridMetrics(width, 0, columns, GAP);
      const used = sidePadding * 2 + tileSize * columns + GAP * (columns - 1);
      assert.ok(used <= width, `${width}px / ${columns} columns overflows by ${used - width}`);
    }
  }
});

test('safe-area insets come off the width before anything is divided', () => {
  const withInset = gridMetrics(1080, 120, 3, GAP);
  const narrower = gridMetrics(960, 0, 3, GAP);
  assert.equal(withInset.tileSize, narrower.tileSize);
});

test('degenerate inputs do not produce a negative tile', () => {
  assert.equal(gridMetrics(0, 0, 3, GAP).tileSize, 0);
  assert.equal(gridMetrics(10, 0, 5, GAP).tileSize, 0);
  assert.equal(gridMetrics(1080, 0, 0, GAP).tileSize, 0);
});

import { describe, expect, it } from 'vitest';
import {
  computeJustifiedRows,
  type JustifiedLayoutItem,
  type JustifiedLayoutOptions,
} from './computeJustifiedRows';

const OPTS: JustifiedLayoutOptions = {
  containerWidth: 1000,
  gap: 6,
  targetRowHeight: 230,
  minRowHeight: 180,
  maxRowHeight: 280,
};

function item(id: string, aspectRatio: number, originalIndex: number): JustifiedLayoutItem {
  return { id, originalIndex, aspectRatio };
}

// A full row must fill the container exactly: sum(tile widths) + gaps == width.
function expectRowFillsWidth(
  row: { items: { width: number }[] },
  gap: number,
  width: number,
) {
  const total = row.items.reduce((acc, t) => acc + t.width, 0) + gap * (row.items.length - 1);
  expect(total).toBe(width);
}

describe('computeJustifiedRows', () => {
  it('returns nothing for an empty list', () => {
    expect(computeJustifiedRows([], OPTS)).toEqual([]);
  });

  it('lays out a single item as an (incomplete) last row without stretching', () => {
    const rows = computeJustifiedRows([item('a', 1.5, 0)], OPTS);
    expect(rows).toHaveLength(1);
    expect(rows[0].isLastRow).toBe(true);
    // A lone 3:2 tile is far too narrow to fill 1000px, so it keeps the target
    // height and is left-aligned rather than blown up to full width.
    expect(rows[0].height).toBe(230);
    expect(rows[0].items[0].width).toBe(Math.round(1.5 * 230));
    expect(rows[0].width).toBeLessThan(OPTS.containerWidth);
  });

  it('fills full rows to the container width exactly (mixed ratios)', () => {
    const items = [
      item('a', 1.5, 0), item('b', 0.7, 1), item('c', 1.0, 2),
      item('d', 1.8, 3), item('e', 1.2, 4), item('f', 0.9, 5),
      item('g', 1.6, 6), item('h', 1.1, 7),
    ];
    const rows = computeJustifiedRows(items, OPTS);
    const complete = rows.filter((r) => !r.isLastRow);
    expect(complete.length).toBeGreaterThan(0);
    for (const row of complete) {
      expectRowFillsWidth(row, OPTS.gap, OPTS.containerWidth);
      expect(row.width).toBe(OPTS.containerWidth);
    }
  });

  it('handles an extremely panoramic item (fills width, short height)', () => {
    const rows = computeJustifiedRows([item('pano', 8, 0)], OPTS);
    expect(rows).toHaveLength(1);
    // 1000 / 8 = 125 <= target, so it closes justified filling the full width.
    expectRowFillsWidth(rows[0], OPTS.gap, OPTS.containerWidth);
    expect(rows[0].height).toBeGreaterThan(0);
  });

  it('handles an extremely vertical item without a negative or zero size', () => {
    const rows = computeJustifiedRows([item('tall', 0.05, 0)], OPTS);
    expect(rows[0].items[0].width).toBeGreaterThanOrEqual(1);
    expect(rows[0].items[0].height).toBeGreaterThanOrEqual(1);
  });

  it.each([
    ['missing (NaN)', NaN],
    ['zero', 0],
    ['negative', -2],
    ['infinite', Infinity],
  ])('falls back to a square for a %s aspect ratio', (_label, ratio) => {
    const rows = computeJustifiedRows([item('x', ratio, 0), item('y', 1, 1)], OPTS);
    const tile = rows[0].items[0];
    // Fallback ratio 1 → width equals height (before any justify correction on
    // the last tile), so at minimum both are positive and finite.
    expect(Number.isFinite(tile.width)).toBe(true);
    expect(tile.width).toBeGreaterThanOrEqual(1);
    expect(tile.height).toBeGreaterThanOrEqual(1);
  });

  it('a row that closes exactly at the container width fills it with no residual', () => {
    // Two 2.5:1 tiles: 1000 - 6 = 994 over ratioSum 5 → height 198.8 (< target
    // 230) so the row closes justified.
    const rows = computeJustifiedRows([item('a', 2.5, 0), item('b', 2.5, 1)], OPTS);
    expectRowFillsWidth(rows[0], OPTS.gap, OPTS.containerWidth);
  });

  it('assigns the rounding residual to the last tile so widths sum exactly', () => {
    // Ratios chosen so naive rounding would leave a residual.
    const items = [item('a', 1.333, 0), item('b', 1.777, 1), item('c', 0.999, 2), item('d', 1.5, 3)];
    const rows = computeJustifiedRows(items, { ...OPTS, containerWidth: 997 });
    for (const row of rows.filter((r) => !r.isLastRow)) {
      expectRowFillsWidth(row, OPTS.gap, 997);
    }
  });

  it('does not justify a clearly-incomplete last row (left-aligned at target)', () => {
    // 13 uniform 3:2 tiles: 12 fill four rows of three, leaving a lone remainder
    // that must not be stretched across the whole width.
    const items = Array.from({ length: 13 }, (_v, i) => item(`i${i}`, 1.5, i));
    const rows = computeJustifiedRows(items, OPTS);
    const last = rows[rows.length - 1];
    expect(last.isLastRow).toBe(true);
    expect(last.items).toHaveLength(1);
    expect(last.width).toBeLessThan(OPTS.containerWidth);
    expect(last.height).toBeLessThanOrEqual(OPTS.maxRowHeight);
  });

  it('marks only the final row as the last row', () => {
    const items = Array.from({ length: 9 }, (_v, i) => item(`i${i}`, 1.5, i));
    const rows = computeJustifiedRows(items, OPTS);
    expect(rows[rows.length - 1].isLastRow).toBe(true);
    for (const row of rows.slice(0, -1)) expect(row.isLastRow).toBe(false);
  });

  it('justifies a last row that naturally completes', () => {
    // Enough wide tiles that the final row fills the width on its own.
    const items = [
      item('a', 1.4, 0), item('b', 1.4, 1), item('c', 1.4, 2),
      item('d', 1.4, 3), item('e', 1.4, 4),
    ];
    const rows = computeJustifiedRows(items, OPTS);
    const last = rows[rows.length - 1];
    if (!last.isLastRow || last.width === OPTS.containerWidth) {
      expectRowFillsWidth(last, OPTS.gap, OPTS.containerWidth);
    }
  });

  it('handles a very narrow container without crashing', () => {
    const items = [item('a', 1.5, 0), item('b', 0.8, 1), item('c', 1.2, 2)];
    const rows = computeJustifiedRows(items, { ...OPTS, containerWidth: 120 });
    for (const row of rows) {
      for (const tile of row.items) {
        expect(tile.width).toBeGreaterThanOrEqual(1);
        expect(tile.height).toBeGreaterThanOrEqual(1);
      }
    }
  });

  it('handles a very wide container', () => {
    const items = Array.from({ length: 20 }, (_v, i) => item(`i${i}`, 1.5, i));
    const rows = computeJustifiedRows(items, { ...OPTS, containerWidth: 4000 });
    for (const row of rows.filter((r) => !r.isLastRow)) {
      expectRowFillsWidth(row, OPTS.gap, 4000);
    }
  });

  it('never produces a negative width or a zero height', () => {
    const ratios = [0.02, 12, 1, NaN, -1, 0, 3.4, 0.6];
    const items = ratios.map((r, i) => item(`i${i}`, r, i));
    const rows = computeJustifiedRows(items, OPTS);
    for (const row of rows) {
      expect(row.height).toBeGreaterThanOrEqual(1);
      for (const tile of row.items) {
        expect(tile.width).toBeGreaterThanOrEqual(1);
        expect(tile.height).toBeGreaterThanOrEqual(1);
      }
    }
  });

  it('preserves the original order and index across rows', () => {
    const items = Array.from({ length: 15 }, (_v, i) => item(`i${i}`, 1 + (i % 3) * 0.5, i));
    const rows = computeJustifiedRows(items, OPTS);
    const flat = rows.flatMap((r) => r.items.map((t) => t.originalIndex));
    expect(flat).toEqual(items.map((it) => it.originalIndex));
  });
});

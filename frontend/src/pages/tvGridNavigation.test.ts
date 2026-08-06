import { describe, expect, it } from 'vitest';
import type { JustifiedLayoutRow } from '../media/layout/computeJustifiedRows';
import { findNextTvGridItem } from './tvGridNavigation';

// Two rows: row 0 has 3 equal 300px tiles, row 1 has 2 equal 450px tiles, gap 0.
// Centres: row0 a=150 b=450 c=750; row1 d=225 e=675.
function layout(): JustifiedLayoutRow[] {
  const tile = (id: string, width: number, originalIndex: number) => ({ id, originalIndex, width, height: 200 });
  return [
    { key: 'r0', height: 200, width: 900, isLastRow: false, items: [tile('a', 300, 0), tile('b', 300, 1), tile('c', 300, 2)] },
    { key: 'r1', height: 200, width: 900, isLastRow: true, items: [tile('d', 450, 3), tile('e', 450, 4)] },
  ];
}

describe('findNextTvGridItem', () => {
  const rows = layout();

  it('RIGHT/LEFT move within the same row', () => {
    expect(findNextTvGridItem(rows, 0, 'a', 'right')).toBe('b');
    expect(findNextTvGridItem(rows, 0, 'b', 'left')).toBe('a');
  });

  it('does not wrap at the row edges', () => {
    expect(findNextTvGridItem(rows, 0, 'c', 'right')).toBeNull();
    expect(findNextTvGridItem(rows, 0, 'a', 'left')).toBeNull();
  });

  it('DOWN picks the nearest horizontal centre in the next row', () => {
    // a centre 150 → nearest of d(225)/e(675) is d.
    expect(findNextTvGridItem(rows, 0, 'a', 'down')).toBe('d');
    // c centre 750 → nearest is e(675).
    expect(findNextTvGridItem(rows, 0, 'c', 'down')).toBe('e');
    // b centre 450 → d is 225 away, e is 225 away → ties resolve to the first (d).
    expect(findNextTvGridItem(rows, 0, 'b', 'down')).toBe('d');
  });

  it('UP picks the nearest horizontal centre in the previous row', () => {
    // e centre 675 → nearest of a(150)/b(450)/c(750) is c.
    expect(findNextTvGridItem(rows, 0, 'e', 'up')).toBe('c');
    // d centre 225 → nearest is a(150).
    expect(findNextTvGridItem(rows, 0, 'd', 'up')).toBe('a');
  });

  it('has nowhere to go UP from the first row or DOWN from the last', () => {
    expect(findNextTvGridItem(rows, 0, 'a', 'up')).toBeNull();
    expect(findNextTvGridItem(rows, 0, 'e', 'down')).toBeNull();
  });

  it('returns null for an unknown id', () => {
    expect(findNextTvGridItem(rows, 0, 'zzz', 'right')).toBeNull();
  });
});

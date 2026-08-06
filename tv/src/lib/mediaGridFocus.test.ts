import assert from 'node:assert/strict';
import test from 'node:test';
import { buildTvMediaFocusLinks } from './mediaGridFocus.ts';
import type { TvJustifiedRow } from './justifiedMediaRows.ts';

interface Item {
  id: string;
}

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

test('vertical focus always targets the immediately adjacent justified row', () => {
  const rows = [
    row(['a', 'b', 'c'], [120, 330, 150], 0),
    row(['d', 'e'], [360, 252], 3),
    row(['f', 'g', 'h'], [210, 190, 204], 5),
  ];

  const links = buildTvMediaFocusLinks(rows, 4);
  const firstRow = new Set(['a', 'b', 'c']);
  const secondRow = new Set(['d', 'e']);
  const thirdRow = new Set(['f', 'g', 'h']);

  for (const id of firstRow) assert.ok(secondRow.has(links.get(id)?.down ?? ''));
  for (const id of secondRow) {
    assert.ok(firstRow.has(links.get(id)?.up ?? ''));
    assert.ok(thirdRow.has(links.get(id)?.down ?? ''));
  }
  for (const id of thirdRow) assert.ok(secondRow.has(links.get(id)?.up ?? ''));
});

test('vertical focus prefers horizontal overlap and horizontal focus stays in-row', () => {
  const rows = [
    row(['wide', 'right'], [400, 200], 0),
    row(['left', 'middle', 'far'], [150, 270, 172], 2),
  ];

  const links = buildTvMediaFocusLinks(rows, 4);

  assert.equal(links.get('wide')?.down, 'middle');
  assert.equal(links.get('right')?.down, 'far');
  assert.equal(links.get('left')?.right, 'middle');
  assert.equal(links.get('middle')?.left, 'left');
  assert.equal(links.get('far')?.right, undefined);
});

import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  buildGalleryRows,
  rowCountFor,
  rowExtent,
  rowForItemIndex,
} from './galleryRows.ts';

const library = (n: number) => Array.from({ length: n }, (_, i) => ({ id: `item-${i}` }));

// The counts the slice names: the empty and tiny cases, and both sides of the
// two page boundaries where the old implementation was observed to fail.
const COUNTS = [0, 1, 2, 3, 4, 5, 59, 60, 61, 119, 120, 121];
const COLUMN_COUNTS = [3, 4, 5];

test('every item appears exactly once, in order', () => {
  for (const count of COUNTS) {
    for (const columns of COLUMN_COUNTS) {
      const items = library(count);
      const flattened = buildGalleryRows(items, columns).flatMap((row) => row.items);
      assert.deepEqual(
        flattened.map((i) => i.id),
        items.map((i) => i.id),
        `${count} items / ${columns} columns lost or reordered something`,
      );
    }
  }
});

test('the row count is exactly what the geometry implies', () => {
  for (const count of COUNTS) {
    for (const columns of COLUMN_COUNTS) {
      const rows = buildGalleryRows(library(count), columns);
      assert.equal(rows.length, rowCountFor(count, columns), `${count} / ${columns}`);
      assert.equal(rows.length, Math.ceil(count / columns) || 0);
    }
  }
});

test('a row knows where it starts, and only the last one may be short', () => {
  for (const count of COUNTS) {
    for (const columns of COLUMN_COUNTS) {
      const rows = buildGalleryRows(library(count), columns);
      rows.forEach((row, index) => {
        assert.equal(row.rowIndex, index);
        assert.equal(row.firstItemIndex, index * columns);
        const isLast = index === rows.length - 1;
        assert.ok(
          row.items.length === columns || (isLast && row.items.length > 0),
          `${count} / ${columns}: row ${index} has ${row.items.length} items`,
        );
      });
    }
  }
});

test('every valid item index maps into a row that exists', () => {
  // The property the old code violated: it addressed the ROW list with an ITEM
  // index, so past the first `rowCount` items it pointed at nothing.
  for (const count of COUNTS) {
    for (const columns of COLUMN_COUNTS) {
      const rows = buildGalleryRows(library(count), columns);
      for (let itemIndex = 0; itemIndex < count; itemIndex += 1) {
        const row = rowForItemIndex(itemIndex, columns);
        assert.ok(row >= 0 && row < rows.length, `${count}/${columns}: item ${itemIndex} → row ${row}`);
        assert.ok(
          rows[row].items.some((_, offset) => rows[row].firstItemIndex + offset === itemIndex),
          `item ${itemIndex} is not inside row ${row}`,
        );
      }
    }
  }
});

test('THE CRASH: an item index is not a row index', () => {
  // The exact failure. 120 media in 3 columns is 40 internal rows; the old code
  // called scrollToIndex(73) on a list whose highest valid index was 39.
  const count = 120;
  const columns = 3;
  const anchorItemIndex = 73;
  const rows = buildGalleryRows(library(count), columns);

  assert.equal(rows.length, 40);
  assert.ok(anchorItemIndex >= rows.length, 'the regression premise no longer holds');
  assert.equal(rowForItemIndex(anchorItemIndex, columns), 24);
  assert.ok(rowForItemIndex(anchorItemIndex, columns) < rows.length);
});

test('the row extent is the tile plus one seam, and offsets stay finite', () => {
  const extent = rowExtent(134, 2);
  assert.equal(extent, 136);
  for (const count of COUNTS) {
    for (const columns of COLUMN_COUNTS) {
      let previous = -1;
      for (let itemIndex = 0; itemIndex < count; itemIndex += 1) {
        const offset: number = rowForItemIndex(itemIndex, columns) * extent;
        assert.ok(Number.isFinite(offset) && offset >= 0);
        assert.ok(offset >= previous, 'offsets must not go backwards as items advance');
        previous = offset;
      }
    }
  }
});

test('degenerate geometry produces nothing rather than something wrong', () => {
  assert.deepEqual(buildGalleryRows(library(5), 0), []);
  assert.deepEqual(buildGalleryRows([], 3), []);
  assert.equal(rowCountFor(0, 3), 0);
  assert.equal(rowCountFor(10, 0), 0);
  assert.equal(rowForItemIndex(7, 0), 0);
  assert.equal(rowForItemIndex(-3, 3), 0);
});

test('appending a page does not disturb the rows already built', () => {
  // Pagination must be independent of position: page two appends, it does not
  // reshuffle.
  const first = library(60);
  const grown = [...first, ...Array.from({ length: 60 }, (_, i) => ({ id: `item-${60 + i}` }))];
  for (const columns of COLUMN_COUNTS) {
    const before = buildGalleryRows(first, columns);
    const after = buildGalleryRows(grown, columns);
    const sharedRows = Math.floor(first.length / columns);
    for (let row = 0; row < sharedRows; row += 1) {
      assert.deepEqual(after[row].items, before[row].items, `row ${row} changed on append`);
      assert.equal(after[row].firstItemIndex, before[row].firstItemIndex);
    }
  }
});

import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { buildGalleryRows, rowForItemIndex } from './galleryRows.ts';
import {
  anchorFromScroll,
  geometryChanged,
  offsetForAnchor,
  type GalleryGeometry,
} from './galleryPosition.ts';

const items = (n: number) => Array.from({ length: n }, (_, i) => ({ id: `item-${i}` }));
const geometry = (columns: number, rowExtent: number, topPadding = 120): GalleryGeometry => ({
  columns,
  rowExtent,
  topPadding,
});

/** Where the viewport sits when the given item leads the visible row. */
const offsetOfItem = (index: number, g: GalleryGeometry) =>
  g.topPadding + rowForItemIndex(index, g.columns) * g.rowExtent;

// --- rotation ---------------------------------------------------------------

test('every rotation of every size keeps the anchor addressable', () => {
  const list = items(121);
  const geometries = [geometry(3, 136), geometry(4, 112), geometry(5, 96), geometry(3, 150)];
  for (const from of geometries) {
    for (const to of geometries) {
      for (const itemIndex of [0, 1, 59, 60, 61, 73, 119, 120]) {
        const anchor = anchorFromScroll({
          y: offsetOfItem(itemIndex, from),
          geometry: from,
          items: list,
        })!;
        const offset = offsetForAnchor({ anchor, geometry: to, items: list })!;
        assert.ok(
          Number.isFinite(offset) && offset >= 0,
          `item ${itemIndex}: ${from.columns}→${to.columns} gave ${offset}`,
        );
        // And it addresses a row that exists under the new geometry.
        const rows = buildGalleryRows(list, to.columns);
        const row = Math.round((offset - to.topPadding) / to.rowExtent);
        assert.ok(row >= 0 && row < rows.length, `row ${row} of ${rows.length}`);
      }
    }
  }
});

test('a same-column rotation still moves, because the tile size did', () => {
  // Landscape at the same column count is a different row extent, so the
  // gallery must still be repositioned — the old code compared only columns.
  assert.equal(geometryChanged(geometry(3, 136), geometry(3, 190)), true);
  const list = items(120);
  const anchor = anchorFromScroll({ y: offsetOfItem(60, geometry(3, 136)), geometry: geometry(3, 136), items: list })!;
  assert.equal(offsetForAnchor({ anchor, geometry: geometry(3, 190), items: list }), 120 + 20 * 190);
});

// --- pagination -------------------------------------------------------------

test('appending pages never moves an existing position', () => {
  const g = geometry(3, 136);
  const anchor = anchorFromScroll({ y: offsetOfItem(45, g), geometry: g, items: items(60) })!;
  const at60 = offsetForAnchor({ anchor, geometry: g, items: items(60) });
  for (const grown of [120, 180, 240]) {
    assert.equal(
      offsetForAnchor({ anchor, geometry: g, items: items(grown) }),
      at60,
      `growing to ${grown} moved the anchor`,
    );
  }
});

test('an anchor past the first page needs no special case', () => {
  // The page size is not part of the arithmetic. 60 is not a boundary here.
  const g = geometry(3, 136);
  const list = items(180);
  for (const itemIndex of [59, 60, 61, 119, 120, 121, 179]) {
    const anchor = anchorFromScroll({ y: offsetOfItem(itemIndex, g), geometry: g, items: list })!;
    const offset = offsetForAnchor({ anchor, geometry: g, items: list })!;
    assert.equal(offset, offsetOfItem(itemIndex, g), `item ${itemIndex}`);
  }
});

test('a refresh that drops the anchored item asks for no movement', () => {
  const g = geometry(3, 136);
  const anchor = anchorFromScroll({ y: offsetOfItem(90, g), geometry: g, items: items(120) })!;
  assert.equal(offsetForAnchor({ anchor, geometry: g, items: items(30) }), null);
});

// --- the gallery is not the foreground surface ------------------------------

test('a geometry change with no anchor yet is a no-op, not a jump', () => {
  // A gallery can receive dimension changes while a viewer is on top of it.
  // With nothing captured there is nothing to restore, and nothing to reset to
  // zero either.
  const g = geometry(3, 136);
  assert.equal(geometryChanged(null, g), false);
  assert.equal(anchorFromScroll({ y: 0, geometry: g, items: [] }), null);
});

test('rotating while the viewer is open leaves a valid position behind it', () => {
  const list = items(121);
  const portrait = geometry(3, 136);
  const landscape = geometry(5, 96);
  // Captured before the viewer opened.
  const anchor = anchorFromScroll({ y: offsetOfItem(70, portrait), geometry: portrait, items: list })!;
  // The rotation happens while the gallery is behind the viewer.
  const offset = offsetForAnchor({ anchor, geometry: landscape, items: list })!;
  assert.ok(offset > 0 && Number.isFinite(offset));
  // Coming back on the same item must not need a second movement.
  const recaptured = anchorFromScroll({ y: offset, geometry: landscape, items: list })!;
  assert.equal(offsetForAnchor({ anchor: recaptured, geometry: landscape, items: list }), offset);
});

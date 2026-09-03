import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  anchorFromScroll,
  geometryChanged,
  offsetForAnchor,
  type GalleryGeometry,
} from './galleryPosition.ts';

const items = (n: number) => Array.from({ length: n }, (_, i) => ({ id: `item-${i}` }));
const geometry = (columns: number, rowExtent: number, topPadding = 100): GalleryGeometry => ({
  columns,
  rowExtent,
  topPadding,
});

test('an offset resolves to the first item of the row it is showing', () => {
  const g = geometry(3, 136);
  // Exactly at the top of row 5: items 15, 16, 17 — the anchor is 15.
  const anchor = anchorFromScroll({ y: 100 + 5 * 136, geometry: g, items: items(120) })!;
  assert.equal(anchor.itemId, 'item-15');
  assert.equal(anchor.rowProgress, 0);
});

test('progress records how far through the row the viewport has travelled', () => {
  const g = geometry(3, 136);
  const anchor = anchorFromScroll({ y: 100 + 5 * 136 + 68, geometry: g, items: items(120) })!;
  assert.equal(anchor.itemId, 'item-15');
  assert.equal(anchor.rowProgress, 0.5);
});

test('capture and restore are inverses inside the content', () => {
  // Inside the content only: past the last row an offset no longer identifies
  // a row, and clamping to the end is the right answer rather than a round trip
  // (asserted separately below).
  const g = geometry(4, 150);
  const list = items(121);
  for (const y of [100, 250, 1000, 3400, 4600]) {
    const anchor = anchorFromScroll({ y, geometry: g, items: list })!;
    const restored = offsetForAnchor({ anchor, geometry: g, items: list })!;
    assert.ok(Math.abs(restored - y) < 0.001, `y=${y} restored to ${restored}`);
  }
});

test('THE ROTATION: the same item lands in the row its new geometry gives it', () => {
  const list = items(120);
  const portrait = geometry(3, 136);
  const landscape = geometry(5, 96);

  // Looking at item 73 — the index that used to be handed to scrollToIndex on a
  // 40-row list.
  const anchor = anchorFromScroll({
    y: 100 + Math.floor(73 / 3) * 136,
    geometry: portrait,
    items: list,
  })!;
  assert.equal(anchor.itemId, 'item-72', 'the anchor is the row leader, not the exact item');

  const offset = offsetForAnchor({ anchor, geometry: landscape, items: list })!;
  // item-72 is index 72; at five columns that is row 14.
  assert.equal(offset, 100 + 14 * 96);
  assert.ok(Number.isFinite(offset) && offset >= 0);
});

test('rotating back and forth is stable, not cumulative', () => {
  const list = items(121);
  const portrait = geometry(3, 136);
  const landscape = geometry(5, 96);
  let anchor = anchorFromScroll({ y: 100 + 20 * 136, geometry: portrait, items: list })!;
  const original = anchor.itemId;
  for (let turn = 0; turn < 3; turn += 1) {
    const toLandscape = offsetForAnchor({ anchor, geometry: landscape, items: list })!;
    anchor = anchorFromScroll({ y: toLandscape, geometry: landscape, items: list })!;
    const toPortrait = offsetForAnchor({ anchor, geometry: portrait, items: list })!;
    anchor = anchorFromScroll({ y: toPortrait, geometry: portrait, items: list })!;
  }
  // The row leader can shift within its own row as the columns change; it must
  // never drift away across repeated turns.
  const drift = Math.abs(Number(anchor.itemId.split('-')[1]) - Number(original.split('-')[1]));
  assert.ok(drift <= 5, `drifted by ${drift} items over three rotations`);
});

test('a width change alone still counts as new geometry', () => {
  // Same column count, different tile size: the rows move even though nothing
  // about the grouping did.
  assert.equal(geometryChanged(geometry(3, 136), geometry(3, 150)), true);
  assert.equal(geometryChanged(geometry(3, 136), geometry(5, 136)), true);
  assert.equal(geometryChanged(geometry(3, 136, 100), geometry(3, 136, 140)), true);
  assert.equal(geometryChanged(geometry(3, 136), geometry(3, 136)), false);
  // Nothing to compare against yet is not a change.
  assert.equal(geometryChanged(null, geometry(3, 136)), false);
});

test('an anchor that no longer exists asks for no movement', () => {
  const g = geometry(3, 136);
  const anchor = { itemId: 'item-999', rowProgress: 0.25 };
  assert.equal(offsetForAnchor({ anchor, geometry: g, items: items(60) }), null);
});

test('overscroll at either end is an ordinary position', () => {
  const g = geometry(3, 136);
  const list = items(61);
  const top = anchorFromScroll({ y: -220, geometry: g, items: list })!;
  assert.equal(top.itemId, 'item-0');
  assert.equal(top.rowProgress, 0);
  const bottom = anchorFromScroll({ y: 100 + 900 * 136, geometry: g, items: list })!;
  assert.equal(bottom.itemId, 'item-60', 'the last row leads at the bottom');
});

test('pagination moves nothing that was already placed', () => {
  const g = geometry(3, 136);
  const before = items(60);
  const after = items(180);
  const anchor = anchorFromScroll({ y: 100 + 10 * 136, geometry: g, items: before })!;
  assert.equal(
    offsetForAnchor({ anchor, geometry: g, items: after }),
    offsetForAnchor({ anchor, geometry: g, items: before }),
    'appending a page moved an existing anchor',
  );
});

test('an empty or degenerate gallery answers null rather than a number', () => {
  assert.equal(anchorFromScroll({ y: 0, geometry: geometry(3, 136), items: [] }), null);
  assert.equal(anchorFromScroll({ y: 0, geometry: geometry(0, 136), items: items(9) }), null);
  assert.equal(anchorFromScroll({ y: 0, geometry: geometry(3, 0), items: items(9) }), null);
  assert.equal(
    offsetForAnchor({ anchor: { itemId: 'item-0', rowProgress: 0 }, geometry: geometry(0, 1), items: items(3) }),
    null,
  );
});

test('a nonsense progress cannot produce a nonsense offset', () => {
  const g = geometry(3, 136);
  for (const rowProgress of [Number.NaN, Number.POSITIVE_INFINITY, -5, 42]) {
    const offset = offsetForAnchor({ anchor: { itemId: 'item-6', rowProgress }, geometry: g, items: items(60) })!;
    assert.ok(Number.isFinite(offset) && offset >= 0, `progress ${rowProgress} gave ${offset}`);
  }
});

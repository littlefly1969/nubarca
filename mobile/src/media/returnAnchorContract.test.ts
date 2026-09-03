import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';
import { ViewerSequenceModel, type ViewerSlide } from './viewerSequence.ts';
import { anchorIndexOf, rowOf } from './galleryAnchor.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

const slide = (key: string): ViewerSlide => ({
  key,
  kind: 'image',
  displayName: key,
  imagePath: `/p/${key}`,
  videoSource: null,
  posterUrl: null,
});

/** A library of thirty items, as a gallery and as a viewer sequence. */
const LIBRARY = Array.from({ length: 30 }, (_, i) => `item-${i}`);
const gallery = LIBRARY.map((id) => ({ id }));
const sequence = LIBRARY.map(slide);

/** The whole journey: open something, maybe swipe, close, come back. */
function journey(openKey: string, swipeTo?: number): string | null {
  const model = new ViewerSequenceModel();
  model.open(sequence, openKey);
  if (swipeTo !== undefined) model.setIndex(swipeTo);
  model.close();
  return model.takeReturnAnchor();
}

test('open and close returns the same item', () => {
  assert.equal(journey('item-24'), 'item-24');
  assert.equal(anchorIndexOf(gallery, journey('item-24')), 24);
});

test('open, swipe, close returns where the user actually was', () => {
  // The reported defect: it returned item 24 after the user had swiped to 29.
  assert.equal(journey('item-24', 29), 'item-29');
  assert.equal(anchorIndexOf(gallery, journey('item-24', 29)), 29);
});

test('the returned item lands correctly in a NEW column geometry', () => {
  // Rotating changes the column count. The anchor is an id, so it survives —
  // and its row is recomputed rather than carried over as a stale offset.
  const anchor = journey('item-24', 29);
  const index = anchorIndexOf(gallery, anchor)!;
  assert.equal(rowOf(index, 3), 9);
  assert.equal(rowOf(index, 4), 7);
  assert.equal(rowOf(index, 5), 5);
});

test('an anchor that no longer exists fails safely', () => {
  // Deleted while the viewer was open, or removed by a filter change. The
  // gallery must stay where it is rather than throw or jump to the top.
  const shrunk = gallery.slice(0, 10);
  assert.equal(anchorIndexOf(shrunk, journey('item-24', 29)), null);
});

test('an account boundary clears a pending return', () => {
  const model = new ViewerSequenceModel();
  model.open(sequence, 'item-5');
  model.close();
  model.reset();
  assert.equal(model.takeReturnAnchor(), null);
});

test('every gallery uses the same mechanism, not four of its own', () => {
  for (const screen of [
    ['app', '(tabs)', 'photos.tsx'],
    ['app', '(tabs)', 'videos.tsx'],
    ['app', 'album', '[id].tsx'],
    ['app', 'shared-album', '[id].tsx'],
  ]) {
    const source = read(...screen);
    assert.match(source, /useReturnAnchor\(\)/, `${screen.join('/')} has no return anchor`);
  }
  // Three of them hand it to the shared grid; the shared album keeps its own
  // list and so wires the same helpers explicitly.
  for (const screen of [
    ['app', '(tabs)', 'photos.tsx'],
    ['app', '(tabs)', 'videos.tsx'],
    ['app', 'album', '[id].tsx'],
  ]) {
    assert.match(read(...screen), /anchorItemId=\{returnAnchor\.itemId\}/);
  }
  assert.match(read('app', 'shared-album', '[id].tsx'), /anchorIndexOf\(/);
});

test('the grid keeps its position outside the list that remounts', () => {
  // `key={columns}` is required to change numColumns and resets the list. The
  // anchor therefore cannot live inside it.
  const grid = read('src', 'components', 'MediaGrid.tsx');
  assert.match(grid, /const visibleAnchor = useRef<string \| null>\(null\)/);
  assert.match(grid, /const pendingAnchor = useRef<string \| null>\(null\)/);
  assert.match(grid, /onViewableItemsChanged/);
  // Restoring must never fetch anything.
  const restore = grid.slice(grid.indexOf('const restoreAnchor'), grid.indexOf('return ('));
  assert.doesNotMatch(restore, /refresh|loadMore|fetch/);
});

test('an arriving anchor is applied, not merely recorded', () => {
  // THE DEFECT THIS PREVENTS. The restore was driven only by
  // `onContentSizeChange`, and returning from the viewer changes neither the
  // content size nor the column count — so the anchor was stored and then
  // nothing ever asked for it. It looked implemented and did nothing.
  for (const [file, effectMarker] of [
    [['src', 'components', 'MediaGrid.tsx'], 'anchorItemId === null'],
    [['app', 'shared-album', '[id].tsx'], 'returnAnchor.itemId === null'],
  ] as [string[], string][]) {
    const source = read(...file);
    const at = source.indexOf(effectMarker);
    assert.ok(at > 0, `${file.join('/')} has no arriving-anchor effect`);
    const effect = source.slice(at, at + 320);
    assert.match(
      effect,
      /restoreAnchor\(\)/,
      `${file.join('/')} records the anchor without applying it`,
    );
  }
});

test('a column change asks immediately as well as on the next layout', () => {
  // A remounted list may not be measured yet; failing to scroll re-arms and the
  // content-size change asks again. Both paths must exist.
  const grid = read('src', 'components', 'MediaGrid.tsx');
  const columnsEffect = grid.slice(grid.indexOf('previousColumns.current === columns'));
  assert.match(columnsEffect.slice(0, 260), /restoreAnchor\(\)/);
  assert.match(grid, /onScrollToIndexFailed/);
  assert.match(grid, /onContentSizeChange=\{restoreAnchor\}/);
});

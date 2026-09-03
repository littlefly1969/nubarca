import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';
import { ViewerSequenceModel, type ViewerSlide } from './viewerSequence.ts';
import { rowForItemIndex } from './galleryRows.ts';

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
  assert.equal(gallery.findIndex((i) => i.id === journey('item-24')), 24);
});

test('open, swipe, close returns where the user actually was', () => {
  // The reported defect: it returned item 24 after the user had swiped to 29.
  assert.equal(journey('item-24', 29), 'item-29');
  assert.equal(gallery.findIndex((i) => i.id === journey('item-24', 29)), 29);
});

test('the returned item lands correctly in a NEW column geometry', () => {
  // Rotating changes the column count. The anchor is an id, so it survives —
  // and its row is recomputed rather than carried over as a stale offset.
  const anchor = journey('item-24', 29);
  const index = gallery.findIndex((i) => i.id === anchor);
  assert.equal(rowForItemIndex(index, 3), 9);
  assert.equal(rowForItemIndex(index, 4), 7);
  assert.equal(rowForItemIndex(index, 5), 5);
});

test('an anchor that no longer exists fails safely', () => {
  // Deleted while the viewer was open, or removed by a filter change. The
  // gallery must stay where it is rather than throw or jump to the top.
  const shrunk = gallery.slice(0, 10);
  assert.equal(shrunk.findIndex((i) => i.id === journey('item-24', 29)), -1);
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
  // ALL FOUR hand it to the same engine now. The shared album used to carry a
  // second copy of the position algorithm, which is how one defect needed two
  // fixes; there is one gallery position engine.
  for (const screen of [
    ['app', '(tabs)', 'photos.tsx'],
    ['app', '(tabs)', 'videos.tsx'],
    ['app', 'album', '[id].tsx'],
    ['app', 'shared-album', '[id].tsx'],
  ]) {
    assert.match(
      read(...screen),
      /anchorItemId=\{returnAnchor\.itemId\}/,
      `${screen.join('/')} does not hand its anchor to the engine`,
    );
  }
});

test('the list survives a rotation instead of being rebuilt by it', () => {
  // NUBARCA-UX-01.2. Explicit rows in a single-column list mean the instance
  // lives through a geometry change; only its data and layout change. The
  // previous design destroyed it with `key={columns}`, and the new list then
  // reported its own first row as the position — which is how "restore" landed
  // on the first photo.
  const engine = read('src', 'components', 'VirtualizedGalleryRows.tsx');
  assert.doesNotMatch(engine, /key=\{columns\}|numColumns/);
  assert.match(engine, /buildGalleryRows\(items, columns\)/);
  assert.match(engine, /geometryChanged\(activeGeometry\.current, geometry\)/);
  // Position is a computed offset, never an index into a list whose index
  // space is rows.
  assert.match(engine, /scrollToOffset\(\{ offset, animated: false \}\)/);
  assert.doesNotMatch(engine, /scrollToIndex/);
  // And the restore path itself may not fetch. Scoped to that path: the engine
  // legitimately accepts a `refreshControl` it never invokes.
  const restore = engine.slice(
    engine.indexOf('geometryChanged(activeGeometry.current, geometry)'),
    engine.indexOf('const renderRow'),
  );
  assert.doesNotMatch(restore, /loadMore|fetch\(|refetch|\brefresh\(/);
});

test('an arriving anchor is applied, not merely recorded', () => {
  // THE DEFECT THIS PREVENTS. The restore was once driven only by
  // `onContentSizeChange`, and returning from the viewer changes neither the
  // content size nor the column count — so the anchor was stored and nothing
  // ever asked for it. It looked implemented and did nothing.
  const engine = read('src', 'components', 'VirtualizedGalleryRows.tsx');
  const at = engine.indexOf('anchorItemId !== null && anchorItemId !== lastAnchorRequest.current');
  assert.ok(at > 0, 'the engine has no viewer-return path');
  const block = engine.slice(at, at + 600);
  assert.match(block, /offsetForAnchor\(/, 'the anchor is recorded without being resolved');
  assert.match(block, /scrollToOffset/, 'the anchor is resolved without being applied');
  assert.match(block, /onAnchorConsumed\?\.\(\)/, 'the anchor is never consumed');
});

test('a geometry change is one scroll, not a retry loop', () => {
  // The previous design recovered through `onScrollToIndexFailed`,
  // `onContentSizeChange` and a pending ref — position restoration driven by
  // unrelated layout events. One computed offset replaces all of it.
  const engine = read('src', 'components', 'VirtualizedGalleryRows.tsx');
  assert.doesNotMatch(engine, /onScrollToIndexFailed|onContentSizeChange/);
  // Incoming scroll during a restore belongs to the replay, not to the user.
  assert.match(engine, /if \(restoring\.current\) return;/);
});

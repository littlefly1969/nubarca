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
  model.open(sequence, openKey, 'photos');
  if (swipeTo !== undefined) model.setIndex(swipeTo);
  model.close();
  return model.takeReturnPosition('photos')?.focusedKey ?? null;
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
  model.open(sequence, 'item-5', 'photos');
  model.close();
  model.reset();
  assert.equal(model.takeReturnPosition('photos')?.focusedKey ?? null, null);
});

test('every gallery uses the same mechanism, not four of its own', () => {
  for (const screen of [
    ['app', '(tabs)', 'photos.tsx'],
    ['app', '(tabs)', 'videos.tsx'],
    ['app', 'album', '[id].tsx'],
    ['app', 'shared-album', '[id].tsx'],
  ]) {
    const source = read(...screen);
    assert.match(
      source,
      /useReturnAnchor\((GALLERY_SCOPE|galleryScope)\)/,
      `${screen.join('/')} does not scope its return anchor`,
    );
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
  const at = engine.indexOf('if (anchorItemId === null) return;');
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

test('a return position belongs to the gallery that opened it', () => {
  // A viewer opened from one library must not leave an anchor another consumes:
  // the ids mean different things in each.
  const model = new ViewerSequenceModel();
  model.open(sequence, 'item-5', 'videos');
  model.setIndex(9);
  model.close();
  assert.equal(model.takeReturnPosition('photos'), null, 'the wrong gallery took it');
  // And it is still there for the one it belongs to.
  assert.equal(model.takeReturnPosition('videos')?.focusedKey, 'item-9');
  assert.equal(model.takeReturnPosition('videos'), null, 'it was not one-shot');
});

test('closing on the item that was opened asks for no movement', () => {
  // The gallery is already where the user left it. Scrolling it "back" to an
  // item they never left is a jump nobody asked for.
  const model = new ViewerSequenceModel();
  model.open(sequence, 'item-24', 'photos');
  model.close();
  const position = model.takeReturnPosition('photos')!;
  assert.equal(position.openedKey, position.focusedKey);
});

test('closing after a swipe asks to move, and says where', () => {
  const model = new ViewerSequenceModel();
  model.open(sequence, 'item-24', 'photos');
  model.setIndex(29);
  model.close();
  const position = model.takeReturnPosition('photos')!;
  assert.equal(position.openedKey, 'item-24');
  assert.equal(position.focusedKey, 'item-29');
  assert.notEqual(position.openedKey, position.focusedKey);
});

test('the hook moves nothing when the item never changed', () => {
  const hook = read('src', 'media', 'useReturnAnchor.ts');
  assert.match(hook, /if \(position\.focusedKey === position\.openedKey\) return;/);
  assert.match(hook, /viewer\.takeReturnPosition\(scopeKey\)/);
});

test('the engine never scrolls or notifies during a render', () => {
  // THE DEFECT THIS PREVENTS. Both restores once ran in the render body, and
  // the viewer-return one called the parent's `onAnchorConsumed` there — a
  // setState during another component's render. React re-enters, and the churn
  // starves the virtualization batches that fill the screen: blank tiles, a
  // list that stops producing content, and a gallery that only recovers when a
  // rotation forces a full re-render.
  const engine = read('src', 'components', 'VirtualizedGalleryRows.tsx');
  const body = engine.slice(engine.indexOf('const listRef'), engine.indexOf('return ('));
  // Every scroll command and every parent notification is inside an effect.
  for (const call of ['scrollToOffset', 'onAnchorConsumed?.()']) {
    let from = 0;
    for (;;) {
      const at = body.indexOf(call, from);
      if (at === -1) break;
      const preceding = body.slice(0, at);
      const lastEffect = preceding.lastIndexOf('useEffect(');
      const lastClose = preceding.lastIndexOf('  }, [');
      assert.ok(
        lastEffect > lastClose,
        `${call} at ${at} is outside a useEffect — it would run during render`,
      );
      from = at + call.length;
    }
  }
});

test('the declared geometry starts where the content starts', () => {
  // The first version's getItemLayout omitted the top padding, so every frame
  // it reported was one chrome-height too high and the window RN chose did not
  // contain the rows on screen. The extent was right; the ORIGIN was not, and
  // the test only checked the extent.
  const engine = read('src', 'components', 'VirtualizedGalleryRows.tsx');
  assert.match(engine, /offset: contentPaddingTop \+ extent \* index/);
  // Sound only while the list has no unmeasured header shifting the content.
  assert.doesNotMatch(engine, /ListHeaderComponent/);
});

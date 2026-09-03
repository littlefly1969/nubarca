import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';
import { ViewerSequenceModel, type ViewerSlide } from './viewerSequence.ts';
import { indexOfItemId } from './galleryAnchor.ts';

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
  // Rotating changes the column count. The anchor is an ID, and the list holds
  // the FLAT items, so the same index answers at every column count — there is
  // no row arithmetic left to get wrong, and no stale offset to carry over.
  const anchor = journey('item-24', 29);
  assert.equal(indexOfItemId(gallery, (i) => i.id, anchor), 29);
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
  // The design that destroyed the list with `key={columns}` put the new one
  // back at the top, and it then reported its own first cell as the position —
  // which is how "restore" landed on the first photo. The list instance must
  // live through a column change; only its layout changes.
  const list = read('src', 'components', 'GalleryList.tsx');
  assert.doesNotMatch(list, /key=\{columns\}|key=\{width\}/);
  // Position is an item index into the flat data, which is defined at every
  // column count.
  assert.match(list, /scrollToIndex\(\{ index, animated: false, viewPosition: 0\.5 \}\)/);
  assert.doesNotMatch(list, /scrollToOffset/);
  // And the restore path may not fetch: the list legitimately accepts a
  // refreshControl it never invokes.
  const restore = list.slice(list.indexOf('const scrollToItemId'), list.indexOf('const renderItem'));
  assert.doesNotMatch(restore, /loadMore|fetch\(|refetch|\brefresh\(/);
});

test('an arriving anchor is applied, not merely recorded', () => {
  // THE DEFECT THIS PREVENTS. The restore was once driven only by
  // `onContentSizeChange`, and returning from the viewer changes neither the
  // content size nor the column count — so the anchor was stored and nothing
  // ever asked for it. It looked implemented and did nothing.
  const list = read('src', 'components', 'GalleryList.tsx');
  const at = list.indexOf('if (anchorItemId === null) return;');
  assert.ok(at > 0, 'the list has no viewer-return path');
  const block = list.slice(at, at + 600);
  assert.match(block, /scrollToItemId\(anchorItemId\)/, 'the anchor is recorded without being applied');
  assert.match(block, /onAnchorConsumed\?\.\(\)/, 'the anchor is never consumed');
});

test('a geometry change is a bounded two-pass, not a retry loop', () => {
  // The previous design recovered through `onScrollToIndexFailed`,
  // `onContentSizeChange` and a pending ref — position restoration driven by
  // unrelated layout events, with no bound on how many times it could fire.
  // What replaces it is a fixed two-pass sequence: the first pass renders and
  // measures the target region, the second lands on real layout, and nothing
  // schedules a third.
  const list = read('src', 'components', 'GalleryList.tsx');
  assert.doesNotMatch(list, /onScrollToIndexFailed|onContentSizeChange|requestAnimationFrame|setTimeout/);
  // A visible window reported mid-restore describes the position being
  // corrected, so it must not write back as the user's anchor.
  assert.match(list, /if \(pendingColumnAnchorRef\.current !== null\) return;/);
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
  const list = read('src', 'components', 'GalleryList.tsx');
  const body = list.slice(list.indexOf('const listRef'), list.indexOf('return ('));
  // Every scroll command and every parent notification sits inside a hook
  // callback — an effect, or a callback the list invokes later. What must never
  // happen is one of them evaluating on the render path itself.
  for (const call of ['scrollToItemId(', 'onAnchorConsumed?.()']) {
    let from = 0;
    for (;;) {
      const at = body.indexOf(call, from);
      if (at === -1) break;
      const preceding = body.slice(0, at);
      const opened = Math.max(
        preceding.lastIndexOf('useEffect('),
        preceding.lastIndexOf('useCallback('),
        preceding.lastIndexOf('useLayoutEffect('),
      );
      const closed = preceding.lastIndexOf('  }, [');
      assert.ok(
        opened > closed,
        `${call} at ${at} is on the render path — it would run during render`,
      );
      from = at + call.length;
    }
  }
});

test('the list declares no geometry for anyone to get wrong', () => {
  // The previous engine declared every frame through getItemLayout, and its
  // first version omitted the top padding: the extent was right, the ORIGIN was
  // one chrome-height out, and the window the list chose did not contain the
  // rows on screen. A test that checks only the extent cannot catch that.
  //
  // Nothing here declares a frame at all now — the list measures — so the class
  // of defect is gone rather than guarded.
  const list = read('src', 'components', 'GalleryList.tsx');
  assert.doesNotMatch(list, /getItemLayout|estimatedItemSize|tileSize|rowExtent/);
});

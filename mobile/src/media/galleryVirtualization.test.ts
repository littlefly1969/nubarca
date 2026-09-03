import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

// The ONE gallery list. Every media surface virtualizes through this file.
const LIST = read('src', 'components', 'GalleryList.tsx');

/** Everything that renders a media gallery, plus the list they all share. */
const GALLERY_LAYER = [
  ['src', 'components', 'GalleryList.tsx'],
  ['src', 'components', 'MediaGrid.tsx'],
  ['app', 'shared-album', '[id].tsx'],
];

test('there is one gallery list, and every surface goes through it', () => {
  // The shared album used to carry its own grid and its own anchor, which is
  // how one defect became two fixes.
  for (const file of [['src', 'components', 'MediaGrid.tsx'], ['app', 'shared-album', '[id].tsx']]) {
    const source = read(...file);
    assert.match(source, /<GalleryList/, `${file.join('/')} does not use the shared list`);
    assert.doesNotMatch(source, /<FlashList/, `${file.join('/')} virtualizes on its own`);
  }
});

test('the list hands FlashList the flat items', () => {
  // The defect this forbids: the previous engine derived a SECOND
  // representation — rows — from the item array, and memoised it. When a page
  // was appended the derived value did not change identity, so the new media
  // simply never appeared. One representation cannot go stale against itself.
  assert.match(LIST, /from '@shopify\/flash-list'/);
  assert.match(LIST, /<FlashList/);
  assert.match(LIST, /data=\{items\}/);
  assert.match(LIST, /numColumns=\{columns\}/);
  assert.match(LIST, /keyExtractor=\{keyOf\}/);
  assert.doesNotMatch(LIST, /<ScrollView/);
});

test('no gallery keeps a second geometry or position engine', () => {
  for (const file of GALLERY_LAYER) {
    const source = read(...file);
    for (const forbidden of [
      /VirtualizedGalleryRows/,
      /buildGalleryRows/,
      /GalleryGeometry/,
      /anchorFromScroll/,
      /offsetForAnchor/,
      /rowProgress/,
      /scrollToOffset/,
      /getItemLayout/,
      /onScrollToIndexFailed/,
      /onContentSizeChange/,
      /key=\{columns\}/,
      /key=\{width\}/,
      /gridMetrics/,
      /requestAnimationFrame/,
      /setTimeout/,
    ]) {
      assert.doesNotMatch(source, forbidden, `${file.join('/')} still uses ${forbidden.source}`);
    }
  }
});

test('the tile is given no geometry to disagree with', () => {
  // MediaTile used to take a pixel `size`, which made every tile depend on the
  // grid's arithmetic and made a rotation invalidate the renderer.
  const tile = read('src', 'components', 'MediaTile.tsx');
  assert.doesNotMatch(tile, /size: number/);
  assert.doesNotMatch(LIST, /tileSize/);
  assert.match(tile, /width: '100%',\s*aspectRatio: 1,/);
});

test('a column change is the only thing that moves the gallery on relayout', () => {
  // Scoping this narrowly is the point. Restoring on append, on selection, on
  // a footer change or on any width change is how a position engine grows back.
  assert.match(
    LIST,
    /useLayoutEffect\(\(\) => \{[\s\S]*?previousColumnsRef\.current === columns[\s\S]*?\}, \[columns\]\);/,
  );
});

test('the restore is a BOUNDED two-pass, addressed by item id', () => {
  // FlashList holds the FLAT items, so an index is an item index. The engine
  // this replaced virtualized ROWS, where the same call addressed nothing:
  // 120 items in 3 columns is 40 rows, so item 73 was out of range and crashed.
  assert.match(LIST, /indexOfItemId/);

  // BOUNDEDNESS IS THE CONTRACT, and counting the call sites is what makes it
  // checkable without pinning one particular promise chain: two scrollToIndex
  // calls exist in the whole file, and both are inside the one restore
  // function. There is no third, and no branch that could produce one.
  const calls = [...LIST.matchAll(/scrollToIndex\(/g)];
  assert.equal(calls.length, 2, `the restore has ${calls.length} scroll passes, not 2`);
  const restore = LIST.slice(LIST.indexOf('const scrollToItemId'), LIST.indexOf('const onCommitLayoutEffect'));
  assert.equal([...restore.matchAll(/scrollToIndex\(/g)].length, 2);

  // The bounded shape is a fixed sequence, never a loop, a schedule or a
  // recursion. Each of these is how a two-pass restore grows into the retry
  // machine this replaced.
  for (const unbounded of [
    /requestAnimationFrame/,
    /setTimeout/,
    /setInterval/,
    /onScrollToIndexFailed/,
    /onContentSizeChange/,
    /attempts|retries|retryCount|passCount/,
    /while \(/,
    /for \(/,
  ]) {
    assert.doesNotMatch(LIST, unbounded, `the restore may grow unbounded via ${unbounded.source}`);
  }
  // No recursion: the restore does not re-enter itself.
  assert.equal([...restore.matchAll(/scrollToItemId\(/g)].length, 0);

  // Armed on the change, and CLEARED BEFORE the restore starts: a still-armed
  // anchor would start a second, competing transaction on the next layout
  // phase — two convergence sequences fighting, each having paused the
  // other's offset correction.
  assert.match(LIST, /pendingColumnAnchorRef\.current = null;\s*scrollToItemId\(id\);/);
});

test('the viewer return takes the same bounded path', () => {
  // Two position commands, one mechanism. A second scroll implementation is
  // how the shared album ended up with its own anchor the first time.
  const viewerReturn = LIST.slice(LIST.indexOf('if (anchorItemId === null) return;'));
  assert.match(viewerReturn, /scrollToItemId\(anchorItemId\)/);
  assert.doesNotMatch(viewerReturn, /scrollToIndex\(/);
});

test('the viewer return is honoured from an effect, never during render', () => {
  // Calling the parent's onAnchorConsumed during render set state on another
  // component mid-render. It belongs in an effect.
  assert.match(LIST, /useEffect\(\(\) => \{[\s\S]*?onAnchorConsumed\?\.\(\);[\s\S]*?\}, \[/);
});

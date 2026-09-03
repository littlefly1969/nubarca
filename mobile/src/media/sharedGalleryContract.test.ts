import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';
import { shouldRefreshOnFocus } from '../lib/focusRefresh.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

const SHARED = read('app', 'shared-album', '[id].tsx');
const OWNED = read('app', 'album', '[id].tsx');

test('a populated paginated list is never replaced by page one on focus', () => {
  // THE DEFECT THIS PREVENTS, and it is a behaviour, not a spelling: load page
  // one, load page two, open an item from page two, come back. An
  // unconditional focus refresh throws the accumulator away and leaves page
  // one — and the return anchor cannot rescue the user, because the item it
  // names is no longer loaded.
  assert.equal(shouldRefreshOnFocus({ itemCount: 0, stale: false }), true);
  assert.equal(shouldRefreshOnFocus({ itemCount: 120, stale: false }), false);
  // A mutation elsewhere still forces the reload it asked for.
  assert.equal(shouldRefreshOnFocus({ itemCount: 120, stale: true }), true);
});

test('the shared album refreshes on focus by the same rule as the owned one', () => {
  // It carried its own policy — an unconditional refresh() — which is exactly
  // the divergence that made one defect need fixing twice.
  for (const [name, source] of [['shared', SHARED], ['owned', OWNED]] as const) {
    assert.match(
      source,
      /shouldRefreshOnFocus\(\{ itemCount: itemCountRef\.current, stale: false \}\)/,
      `${name} album does not use the shared focus rule`,
    );
    assert.match(source, /itemCountRef\.current = snapshot\.items\.length;/);
  }
  // And the refresh is INSIDE the guard, not beside it.
  const focus = SHARED.slice(SHARED.indexOf('useFocusEffect'), SHARED.indexOf('getSharedAlbum(albumId)'));
  assert.match(focus, /if \(shouldRefreshOnFocus\([\s\S]*?\)\) \{\s*void refresh\(\);\s*\}/);
});

test('a filter change still restarts pagination for the slice it selected', () => {
  // The focus rule must not disarm the one refresh that is always correct: a
  // new query generation has no accumulator worth keeping.
  // (the reader strips comments, so this matches the effect body itself)
  assert.match(SHARED, /useEffect\(\(\) => \{\s*void refresh\(\);\s*\}, \[kind\]\);/);
  // And the fetcher is rebuilt for the new kind, so the restarted pagination
  // asks the server for the right slice.
  assert.match(SHARED, /\[albumId, kind\],/);
});

test('the shared tile brings no gallery spacing of its own', () => {
  // GalleryList already puts half a gutter on the content and half on each
  // cell. A margin here made the shared album's seam wider than Photos and
  // Videos — a second geometry layer, which is the thing this whole rewrite
  // removed.
  const tile = SHARED.slice(SHARED.indexOf('    tile: {'), SHARED.indexOf('    tileImg:'));
  assert.doesNotMatch(tile, /margin|padding/);
  assert.match(tile, /width: '100%',\s*aspectRatio: 1,/);
});

test('the gallery seam is owned in exactly one place', () => {
  const list = read('src', 'components', 'GalleryList.tsx');
  assert.match(list, /paddingHorizontal: grid\.gap \/ 2/);
  assert.match(list, /paddingBottom: grid\.gap/);
  // No media surface computes a seam beside it.
  for (const [name, source] of [
    ['shared album', SHARED],
    ['media grid', read('src', 'components', 'MediaGrid.tsx')],
    ['media tile', read('src', 'components', 'MediaTile.tsx')],
  ] as const) {
    assert.doesNotMatch(source, /grid\.gap/, `${name} computes its own gallery seam`);
  }
});

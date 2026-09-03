import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

const GRID = read('src', 'components', 'MediaGrid.tsx');

/**
 * Files that own a media gallery list. The shared album joins this list when
 * it stops carrying its own grid; until then it is migrated separately, not
 * exempted.
 */
const GALLERY_LAYER = [['src', 'components', 'MediaGrid.tsx']];

test('the grid hands FlashList the flat media items', () => {
  // The defect this forbids: the previous engine derived a SECOND
  // representation — rows — from the item array, and memoised it. When a page
  // was appended the derived value did not change identity, so the new media
  // simply never appeared. One representation cannot go stale against itself.
  assert.match(GRID, /from '@shopify\/flash-list'/);
  assert.match(GRID, /<FlashList/);
  assert.match(GRID, /data=\{items\}/);
  assert.match(GRID, /numColumns=\{columns\}/);
  assert.match(GRID, /const keyOf = useCallback\(\(item: MediaItem\) => item\.id, \[\]\)/);
  assert.match(GRID, /keyExtractor=\{keyOf\}/);
  assert.doesNotMatch(GRID, /<ScrollView/);
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
  assert.doesNotMatch(GRID, /size=\{/);
  assert.match(tile, /width: '100%',\s*aspectRatio: 1,/);
});

test('a column change is the only thing that moves the gallery on relayout', () => {
  // Scoping this narrowly is the point. Restoring on append, on selection, on
  // a footer change or on any width change is how a position engine grows back.
  assert.match(
    GRID,
    /useLayoutEffect\(\(\) => \{[\s\S]*?previousColumnsRef\.current === columns[\s\S]*?\}, \[columns\]\);/,
  );
});

test('the restore is one scroll per transition, addressed by item id', () => {
  // FlashList holds the FLAT items, so an index is an item index. The engine
  // this replaced virtualized ROWS, where the same call addressed nothing:
  // 120 items in 3 columns is 40 rows, so item 73 was out of range and crashed.
  assert.match(GRID, /indexOfItemId/);
  // Armed on the change, cleared before the scroll: a still-armed anchor would
  // start a second, competing scroll on the next layout commit.
  assert.match(GRID, /pendingColumnAnchorRef\.current = null;\s*scrollToItemId\(id\);/);
});

test('the viewer return is honoured from an effect, never during render', () => {
  // Calling the parent's onAnchorConsumed during render set state on another
  // component mid-render. It belongs in an effect.
  assert.match(GRID, /useEffect\(\(\) => \{[\s\S]*?onAnchorConsumed\?\.\(\);[\s\S]*?\}, \[/);
});

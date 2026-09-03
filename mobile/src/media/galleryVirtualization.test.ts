import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

/** Every file allowed to virtualize a media gallery. */
const GALLERY_LAYER = [
  ['src', 'components', 'VirtualizedGalleryRows.tsx'],
  ['src', 'components', 'MediaGrid.tsx'],
  ['app', 'shared-album', '[id].tsx'],
];

test('no gallery addresses a row list with an item index', () => {
  // THE CRASH THIS FORBIDS. `FlatList numColumns` virtualizes ROWS, and its
  // `scrollToIndex` forwards the index straight to that row list. Passing a
  // media index is out of range by construction: 120 items in 3 columns is 40
  // rows, so item 73 addresses nothing at all.
  //
  // Position is a pixel offset now, which is defined for every item at every
  // geometry. These four constructs are how the old design comes back.
  for (const file of GALLERY_LAYER) {
    const source = read(...file);
    for (const forbidden of [
      /scrollToIndex\(/,
      /onScrollToIndexFailed/,
      /key=\{columns\}/,
      /numColumns=\{columns\}/,
    ]) {
      assert.doesNotMatch(source, forbidden, `${file.join('/')} still uses ${forbidden.source}`);
    }
  }
});

test('the shared album does not keep a second position engine', () => {
  // It carried its own copy of the anchor algorithm, which is how one defect
  // became two fixes. There is one gallery position engine.
  const shared = read('app', 'shared-album', '[id].tsx');
  for (const local of [/visibleAnchor/, /pendingAnchor/, /onViewableItemsChanged/]) {
    assert.doesNotMatch(shared, local, `shared album still owns ${local.source}`);
  }
});

test('the row grid declares its geometry instead of measuring it', () => {
  const grid = read('src', 'components', 'VirtualizedGalleryRows.tsx');
  assert.match(grid, /getItemLayout/);
  assert.match(grid, /scrollToOffset/);
  // A retry loop driven by unrelated layout events is what the previous design
  // used in place of knowing where things are.
  assert.doesNotMatch(grid, /onContentSizeChange/);
});

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

function source(relativePath: string): string {
  return readFileSync(new URL(relativePath, import.meta.url), 'utf8');
}

const party = source('../screens/AlbumItemsScreen.tsx');
const gallery = source('../screens/PersonalGalleryScreen.tsx');
const videos = source('../screens/PersonalVideosScreen.tsx');
const focusTile = source('../components/FocusableMediaTile.tsx');

test('all current native media grids share compact proportional presentation', () => {
  for (const screen of [party, gallery, videos]) {
    assert.match(screen, /FocusableMediaTile/);
    assert.match(screen, /buildTvJustifiedRows/);
    assert.match(screen, /MEDIA_GRID_VISUAL_GAP/);
    assert.match(screen, /packingGap:/);
    assert.match(screen, /mediaGridTargetRowHeight\(height\)/);
  }
});

test('photo grids stay on small thumbnails', () => {
  assert.match(party, /const path = isVideo \? .* : item\.thumbnailUrl/);
  assert.match(party, /const fallbackPath = isVideo \? item\.thumbnailUrl : null/);
  assert.match(gallery, /path=\{item\.thumbnailUrl\}/);
  assert.doesNotMatch(gallery, /fallbackPath=\{item\.previewUrl\}/);
});

test('the personal video wall always uses the static poster, never the preview strip', () => {
  assert.match(videos, /path=\{item\.posterUrl\}/);
  assert.doesNotMatch(videos, /previewStripUrl/);
  assert.doesNotMatch(videos, /STRIP_FRAME/);
});

test('media focus rings overlay content instead of reserving layout chrome', () => {
  assert.match(focusTile, /position: 'absolute'/);
  assert.doesNotMatch(focusTile, /\n  inner: \{/);
  assert.doesNotMatch(focusTile, /borderColor: 'transparent'/);
});

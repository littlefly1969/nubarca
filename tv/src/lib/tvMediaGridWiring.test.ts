import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const source = (relativePath: string) => readFileSync(new URL(relativePath, import.meta.url), 'utf8');

const screens = [
  source('../screens/AlbumsScreen.tsx'),
  source('../screens/AlbumItemsScreen.tsx'),
  source('../screens/PersonalAlbumsScreen.tsx'),
  source('../screens/PersonalLibraryScreen.tsx'),
  source('../screens/BeautyLabScreen.tsx'),
];
const preview = source('../components/MediaTilePreview.tsx');
const focusTile = source('../components/FocusableMediaTile.tsx');
const focusHook = source('./useTvMediaGridFocus.ts');
const library = screens[3];

test('every TV wall uses the same proportional layout and readiness-gated focus', () => {
  for (const screen of screens) {
    assert.match(screen, /buildTvMediaGridRows/);
    assert.match(screen, /useTvMediaGridFocus/);
    assert.match(screen, /isRowReady/);
    assert.match(screen, /onPreviewReady/);
    assert.doesNotMatch(screen, /tvFixedGrid|numColumns/);
  }
});

test('media walls use DTO dimensions and album shelves use one stable card ratio', () => {
  assert.match(screens[1], /getAspectRatio: getTvMediaAspectRatio/);
  assert.match(library, /normalizeTvMediaAspectRatio/);
  assert.match(screens[4], /normalizeTvMediaAspectRatio/);
  assert.match(screens[0], /getAspectRatio: \(\) => TILE_ASPECT/);
  assert.match(screens[2], /getAspectRatio: \(\) => TILE_ASPECT/);
});

test('unready and unmounted directions are trapped on the current tile', () => {
  assert.match(focusHook, /readyRows\.has\(targetRow\)/);
  assert.match(focusHook, /: self/);
  assert.match(focusTile, /focusTargets\?\.self\.current \?\? undefined/);
  assert.match(preview, /onLoad=\{\(\) => setDecoded\(true\)\}/);
  assert.match(preview, /onReady\?\.\(\)/);
});

test('every wall prepares one next row before focus can enter it', () => {
  for (const screen of screens) {
    assert.match(screen, /prepareRowAfter/);
    assert.match(screen, /additionalRenderRegions=\{gridFocus\.additionalRenderRegions\}/);
    assert.match(screen, /removeClippedSubviews=\{false\}/);
  }
  assert.match(focusHook, /rowIndex \+ 1/);
  assert.match(focusHook, /\[\{ first: .*last:/);
});

test('All, Photos and Videos live in MENU, not above the library grid', () => {
  assert.doesNotMatch(library, /styles\.tabs/);
  const menu = library.slice(library.indexOf('{overlayVisible && gridInteractive'));
  assert.match(menu, /\(\['all', 'image', 'video'\] as const\)\.map/);
});

test('video tiles remain still-image previews with one viewer player', () => {
  for (const screen of screens) {
    assert.doesNotMatch(screen, /VideoView|useVideoPlayer/);
    assert.doesNotMatch(screen, /previewStripUrl/);
  }
  assert.match(library, /PersonalMediaViewer/);
});

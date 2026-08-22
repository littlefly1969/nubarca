import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';

const source = (relativePath: string) => read(import.meta.url, relativePath);

const screens = [
  ['AlbumsScreen.tsx', source('../screens/AlbumsScreen.tsx')],
  ['AlbumItemsScreen.tsx', source('../screens/AlbumItemsScreen.tsx')],
  ['PersonalAlbumsScreen.tsx', source('../screens/PersonalAlbumsScreen.tsx')],
  ['PersonalLibraryScreen.tsx', source('../screens/PersonalLibraryScreen.tsx')],
  ['BeautyLabScreen.tsx', source('../screens/BeautyLabScreen.tsx')],
] as const;
const screenSources = screens.map(([, screen]) => screen);
const preview = source('../components/MediaTilePreview.tsx');
const focusTile = source('../components/FocusableMediaTile.tsx');
const library = screens[3][1];

test('every TV wall uses the same proportional layout and native TV focus', () => {
  for (const [name, screen] of screens) {
    assert.match(screen, /buildTvMediaGridRows/, name);
    assert.match(screen, /<FocusableMediaTile/, name);
    assert.doesNotMatch(screen, /\buseTvMediaGridFocus\b|\bgridFocus\b|\bfocusTargets\b/, name);
    assert.doesNotMatch(screen, /isRowReady|rowReady|onPreviewReady|prepareRowAfter/, name);
    assert.doesNotMatch(screen, /additionalRenderRegions/, name);
    assert.doesNotMatch(screen, /tvFixedGrid|numColumns/, name);
  }
  assert.doesNotMatch(focusTile, /nextFocus(?:Left|Right|Up|Down)|focusTargets/);
  assert.doesNotMatch(preview, /onReady/);
});

test('every TV wall uses the installed native item-snap scrolling contract', () => {
  for (const [name, screen] of screens) {
    assert.match(screen, /<TVFocusGuideView/, name);
    assert.match(screen, /trapFocusLeft/, name);
    assert.match(screen, /trapFocusRight/, name);
    assert.match(screen, /scrollSnapAlign="start"/, name);
    assert.match(screen, /snapToAlignment="item"/, name);
    assert.match(screen, /scrollAnimationEnabled=\{false\}/, name);
    assert.match(screen, /removeClippedSubviews=\{false\}/, name);
  }
});

test('media walls use DTO dimensions and album shelves use one stable card ratio', () => {
  assert.match(screenSources[1], /getAspectRatio: getTvMediaAspectRatio/);
  assert.match(library, /normalizeTvMediaAspectRatio/);
  assert.match(screenSources[4], /normalizeTvMediaAspectRatio/);
  assert.match(screenSources[0], /getAspectRatio: \(\) => TILE_ASPECT/);
  assert.match(screenSources[2], /getAspectRatio: \(\) => TILE_ASPECT/);
});

test('All, Photos and Videos live in MENU, not above the library grid', () => {
  assert.doesNotMatch(library, /styles\.tabs/);
  const menu = library.slice(library.indexOf('{overlayVisible && gridInteractive'));
  assert.match(menu, /\(\['all', 'image', 'video'\] as const\)\.map/);
});

test('paged walls serialize appends and reject duplicate item ids', () => {
  assert.match(library, /requestedCursorRef\.current === current\.nextCursor/);
  assert.match(library, /known\.has\(item\.id\)/);
  const beauty = screenSources[4];
  assert.match(beauty, /if \(loadInFlightRef\.current\) return/);
  assert.match(beauty, /known\.has\(item\.id\)/);
});

test('video tiles remain still-image previews with one viewer player', () => {
  for (const screen of screenSources) {
    assert.doesNotMatch(screen, /VideoView|useVideoPlayer/);
    assert.doesNotMatch(screen, /previewStripUrl/);
  }
  assert.match(library, /PersonalMediaViewer/);
});

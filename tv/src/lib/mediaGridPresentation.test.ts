import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

function source(relativePath: string): string {
  return readFileSync(new URL(relativePath, import.meta.url), 'utf8');
}

const party = source('../screens/AlbumItemsScreen.tsx');
const library = source('../screens/PersonalLibraryScreen.tsx');
const albums = source('../screens/PersonalAlbumsScreen.tsx');
const focusTile = source('../components/FocusableMediaTile.tsx');

// These assert WIRING, which a unit test of the pure helpers cannot reach: a
// screen can go on building and rendering perfectly while quietly re-adopting
// the geometry that made fast navigation drift.

test('every native media wall uses the ONE deterministic fixed-column grid', () => {
  for (const screen of [party, library, albums]) {
    assert.match(screen, /FocusableMediaTile/);
    assert.match(screen, /buildTvGridRows/);
    assert.match(screen, /tvGridColumns\(contentWidth\)/);
    assert.match(screen, /tvGridTileWidth\(contentWidth, columns, GRID_GAP\)/);
    assert.match(screen, /useTvFixedGridFocus\(/);
  }
});

test('the justified wall and its lane machinery are GONE, not merely unused', () => {
  // A justified row makes adjacent rows split the width at different places, so
  // Android's geometric focus search disagrees with the column the user
  // believes they are in — the whole reason vertical navigation drifted. Its
  // lane workaround then made the focus graph depend on a React render landing
  // between two key presses, which is what made fast and slow diverge.
  for (const screen of [party, library, albums]) {
    assert.doesNotMatch(screen, /buildTvJustifiedRows|justifiedMediaRows/);
    assert.doesNotMatch(screen, /preferredX|laneFocus|noteFocusRestore/);
    assert.doesNotMatch(screen, /mediaGridTargetRowHeight/);
  }
});

test('tiles are uniform, which is what makes the geometric fallback agree', () => {
  // Equal boxes on aligned columns mean Android's own search names the SAME
  // tile the explicit nextFocus link does. A momentarily unresolved link then
  // degrades to an equivalent answer instead of a lateral drift.
  for (const screen of [party, library, albums]) {
    assert.match(screen, /const TILE_ASPECT = /);
    assert.match(screen, /width=\{tileWidth\}/);
    assert.match(screen, /height=\{tileHeight\}/);
  }
});

test('video tiles never mount a player and never use the six-cell sprite', () => {
  // A grid is many tiles; a player per tile is how a constrained Fire Stick
  // runs out of codec sessions and memory. And the preview strip is a
  // 2880x270 sprite — the wrong shape for a tile at any resize mode.
  for (const screen of [party, library, albums]) {
    assert.doesNotMatch(screen, /VideoView|useVideoPlayer/);
    assert.doesNotMatch(screen, /previewStripUrl/);
  }
  assert.match(library, /MediaTilePreview/);
});

test('media focus rings overlay content instead of reserving layout chrome', () => {
  assert.match(focusTile, /position: 'absolute'/);
  assert.doesNotMatch(focusTile, /\n  inner: \{/);
  assert.doesNotMatch(focusTile, /borderColor: 'transparent'/);
});

test('the tile resolves focus targets WITHOUT copying them into state', () => {
  // The old tile stored resolved neighbour Views in useState inside a layout
  // effect, which put a whole extra render between "a neighbour mounted" and
  // "the native link is correct" — one of the two renders a held D-pad could
  // outrun.
  assert.doesNotMatch(focusTile, /setResolvedTargets|resolvedTargets/);
  assert.match(focusTile, /nextFocusDown=\{focusTargets\?\.down\?\.current \?\? undefined\}/);
});

import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const GRID = code(readFileSync(resolve(HERE, 'MediaGrid.tsx'), 'utf8'));
const TILE = code(readFileSync(resolve(HERE, 'MediaTile.tsx'), 'utf8'));

// BRAND-APP-03 changes the media LANGUAGE, not the media engine. Most of this
// file is therefore about what did not move.

test('virtualization survives; the grid no longer tunes it', () => {
  // The media language is not the media engine. UX-01.3 handed rows,
  // recycling and viewport management to FlashList, so the tuning constants
  // this used to pin are gone on purpose — what must not come back is a
  // gallery that renders every tile.
  assert.match(GRID, /<FlashList/);
  assert.doesNotMatch(GRID, /<ScrollView/);
  assert.doesNotMatch(GRID, /windowSize|maxToRenderPerBatch|initialNumToRender/);
});

test('a selection change does not reshuffle the grid', () => {
  // The tile renderer depends on what a tile IS, never on how big it is: the
  // tile squares itself inside the column, so a rotation cannot invalidate it.
  assert.match(
    GRID,
    /\[styles, selecting, selectedIds, onPressItem, onToggleSelect, onLongPressItem\]/,
  );
  assert.match(GRID, /const keyOf = useCallback\(\(item: MediaItem\) => item\.id, \[\]\)/);
  // The list key must not change with the column count: that destroyed the
  // list on every rotation and put it back at the top.
  assert.doesNotMatch(GRID, /key=\{columns\}/);
});

test('the grid is a gallery seam, not a gap between cards', () => {
  // The canonical gallery gutter, not a generic spacing step: a library is a
  // seam between pictures, and at four pixels it starts to read as a set of
  // tiles rather than as one surface. Half on the content and half on each
  // cell makes the outer margin equal the seam, with no per-tile arithmetic.
  assert.match(GRID, /paddingHorizontal: grid\.gap \/ 2/);
  assert.match(GRID, /paddingLeft: insets\.left \+ grid\.gap \/ 2/);
  assert.doesNotMatch(GRID, /gap = spacing\./);
});

test('the tile is a media frame: no card chrome anywhere', () => {
  assert.doesNotMatch(TILE, /shadow(Color|Offset|Opacity|Radius)|elevation:/);
  // No rounding on the frame itself. The badges keep their radius role; the
  // picture does not.
  assert.doesNotMatch(TILE, /tile: \{[^}]*borderRadius/);
});

test('selection is an edge and a control, never a wash over the picture', () => {
  assert.match(TILE, /tileSelected: \{\s*borderWidth: 2,\s*borderColor: colors\.accent,/);
  assert.match(TILE, /checkRingOn: \{\s*backgroundColor: colors\.accentStrong,/);
  // The refused vocabulary.
  assert.doesNotMatch(TILE, /signalConnected|signalIntelligence|media\.highlight/);
  assert.doesNotMatch(TILE, /shadow|glow/i);
});

test('selection is announced, not only drawn', () => {
  assert.match(TILE, /accessibilityState=\{\{ selected: selecting \? selected : undefined \}\}/);
  // The tile carries the name; the image must not announce it again.
  assert.doesNotMatch(TILE, /<AuthedImage[^>]*accessibilityLabel/);
});

test('a video still says what it is, and a missing poster still says so', () => {
  assert.match(TILE, /name="play"/);
  assert.match(TILE, /formatDuration\(item\.durationSeconds\)/);
  assert.match(TILE, /posterSource === 'synthetic' \|\| item\.posterSource === null/);
  assert.match(TILE, /t\('grid\.syntheticPoster'\)/);
});

test('duplicates are arithmetic, so they are neutral', () => {
  // Soft Violet means inference. Spending it on a duplicate count would leave
  // the product with no way to say "a model produced this".
  assert.match(TILE, /dupChip: \{[^}]*backgroundColor: media\.chrome/);
});

test('neither file states a colour, a radius or a type of its own', () => {
  for (const [name, source] of [['MediaGrid', GRID], ['MediaTile', TILE]] as const) {
    assert.doesNotMatch(source, /#[0-9A-Fa-f]{3,8}\b|\brgba?\(/, `${name} has a colour literal`);
    assert.doesNotMatch(source, /\bradii\.|\btype\.(title|sectionTitle|body|secondary|badge)\b|colors\.surfaceMuted/, `${name} uses a deprecated alias`);
    assert.doesNotMatch(source, /fontSize: \d|fontWeight: '[67]00'/, `${name} declares raw type`);
  }
});

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

test('virtualization and its tuning survive', () => {
  assert.match(GRID, /<FlatList/);
  assert.doesNotMatch(GRID, /<ScrollView/);
  assert.match(GRID, /removeClippedSubviews/);
  assert.match(GRID, /windowSize=\{7\}/);
  assert.match(GRID, /maxToRenderPerBatch=\{columns \* 3\}/);
  assert.match(GRID, /initialNumToRender=\{columns \* 2\}/);
  assert.match(GRID, /onEndReachedThreshold=\{0\.5\}/);
});

test('the callbacks keep the identities that stop rows remounting', () => {
  // Selection changing must not reshuffle the grid: the key extractor has no
  // dependencies, and renderItem's list is unchanged.
  assert.match(GRID, /const keyExtractor = useCallback\(\(item: MediaItem\) => item\.id, \[\]\)/);
  assert.match(
    GRID,
    /\[tileSize, selecting, selectedIds, onPressItem, onToggleSelect, onLongPressItem\]/,
  );
  assert.match(GRID, /key=\{columns\}/);
});

test('the grid is a gallery seam, not a gap between cards', () => {
  assert.match(GRID, /const gap = grid\.gap;/);
  assert.doesNotMatch(GRID, /const gap = spacing\./);
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

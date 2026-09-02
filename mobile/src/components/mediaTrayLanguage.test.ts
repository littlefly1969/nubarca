import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const TRAY = code(
  readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), 'MediaSelectionBar.tsx'), 'utf8'),
);

test('the capability matrix is still the only authority for actions', () => {
  // The tray never decides what may be done to a selection; it renders what it
  // is told and nothing more.
  assert.match(TRAY, /MediaSelectionCapabilities/);
  assert.match(TRAY, /actions\.map/);
  assert.doesNotMatch(TRAY, /useState|useReducer/);
});

test('a confirmation still means what it meant', () => {
  assert.match(TRAY, /if \(action\.confirm === undefined\) return action\.run\(\)/);
  assert.match(TRAY, /Alert\.alert\(action\.confirm\.title, action\.confirm\.body/);
  assert.match(TRAY, /style: action\.destructive === true \? 'destructive' : 'default'/);
});

test('zero selected still says what to do rather than showing a bare 0', () => {
  assert.match(TRAY, /count === 0 \? t\('selection\.hint'\) : String\(count\)/);
});

test('the tray sits on the real inset, not a guessed one', () => {
  assert.match(TRAY, /useSafeAreaInsets\(\)/);
  assert.match(TRAY, /paddingBottom: spacing\.s \+ insets\.bottom/);
  assert.doesNotMatch(TRAY, /paddingBottom: 20/);
});

test('it is an operating mode, not a second bottom navigation', () => {
  // Accent text and icon on a quiet surface. The capability matrix can offer
  // several actions at once; a row of filled blue buttons would claim they are
  // all the dominant one.
  assert.match(TRAY, /actionLabel: \{ \.\.\.typography\.label, color: colors\.accent \}/);
  assert.match(TRAY, /backgroundColor: colors\.surfaceSubtle/);
  assert.doesNotMatch(TRAY, /backgroundColor: colors\.accent\b|accentStrong/);
  assert.doesNotMatch(TRAY, /signalConnected|signalIntelligence/);
  assert.doesNotMatch(TRAY, /shadow(Color|Opacity|Radius)|elevation:/);
});

test('destructive stays destructive, and the targets stay reachable', () => {
  assert.match(TRAY, /destructive: \{ color: colors\.danger \}/);
  assert.match(TRAY, /minHeight: touch\.minSize/);
  assert.match(TRAY, /<IconButton/);
});

test('no colour, deprecated alias or raw type of its own', () => {
  assert.doesNotMatch(TRAY, /#[0-9A-Fa-f]{3,8}\b|\brgba?\(/);
  assert.doesNotMatch(TRAY, /\bradii\.|colors\.surfaceMuted/);
  assert.doesNotMatch(TRAY, /fontSize: \d|fontWeight: '[67]00'/);
});

import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const LAYOUT = code(readFileSync(resolve(ROOT, 'app', '(tabs)', '_layout.tsx'), 'utf8'));
const BAR = code(readFileSync(resolve(ROOT, 'src', 'ui', 'BrandTabBar.tsx'), 'utf8'));

test('the five destinations, and their order, are unchanged', () => {
  const names = [...LAYOUT.matchAll(/name="(\w+)"/g)].map((m) => m[1]);
  assert.deepEqual(names, ['photos', 'videos', 'albums', 'files', 'sync']);
});

test('the bar renders the router state and keeps none of its own', () => {
  // A tab bar with its own idea of what is selected disagrees with the router
  // the first time a route changes from anywhere else — a deep link, a
  // redirect, a back gesture.
  assert.match(BAR, /state\.routes\.map/);
  assert.match(BAR, /state\.index === index/);
  assert.doesNotMatch(BAR, /useState|useReducer/);
});

test('a press is the ordinary tab event, and preventDefault is honoured', () => {
  assert.match(BAR, /navigation\.emit\(\{\s*type: 'tabPress',[\s\S]*?canPreventDefault: true,/);
  assert.match(BAR, /!focused && !event\.defaultPrevented/);
  assert.match(BAR, /navigation\.navigate\(route\.name\)/);
  assert.match(BAR, /type: 'tabLongPress'/);
});

test('the selected destination is announced, not merely coloured', () => {
  assert.match(BAR, /accessibilityRole="tab"/);
  assert.match(BAR, /accessibilityState=\{\{ selected: focused \}\}/);
});

test('the touch target keeps the 48 dp class', () => {
  assert.match(BAR, /minHeight: touch\.minSize/);
});

test('the selected state is an edge and an accent, never a capsule or a glow', () => {
  assert.match(BAR, /borderTopColor: colors\.accent/);
  assert.match(BAR, /borderTopWidth: 2/);
  // The things the brand explicitly refuses here.
  assert.doesNotMatch(BAR, /shadow(Color|Opacity|Radius)|elevation:/);
  assert.doesNotMatch(BAR, /backgroundColor: colors\.accent\b/);
  assert.doesNotMatch(BAR, /BlurView|blurRadius/);
});

test('the bar floats over the gallery instead of sitting below it', () => {
  // NUBARCA-UX-01 §5: an opaque bar appended under the content reads as a
  // block bolted to the app; this one overlays, and the media stays
  // perceptible through it.
  assert.match(BAR, /backgroundColor: colors\.surfaceFloating/);
  assert.match(BAR, /position: 'absolute'/);
  assert.match(BAR, /borderTopWidth: StyleSheet\.hairlineWidth/);
  assert.match(BAR, /insets\.bottom/);
  // Without this the navigator still shortens the scene by the bar's height
  // and the gallery ends in a dead band.
  assert.match(LAYOUT, /tabBarStyle: \{ position: 'absolute' \}/);
});

test('the bar publishes the room a gallery must leave for it', () => {
  assert.match(BAR, /export const TAB_BAR_CONTENT_HEIGHT/);
});

test('type and icon sizing come from the contract', () => {
  assert.match(BAR, /\.\.\.typography\.badge/);
  assert.match(BAR, /size: iconSizes\.l/);
  assert.doesNotMatch(BAR, /fontSize: \d/);
  assert.doesNotMatch(BAR, /\bradii\.|colors\.surfaceMuted/);
});

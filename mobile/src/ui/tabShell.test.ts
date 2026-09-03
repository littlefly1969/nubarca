import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const LAYOUT = code(readFileSync(resolve(ROOT, 'app', '(tabs)', '_layout.tsx'), 'utf8'));
const BAR = code(readFileSync(resolve(ROOT, 'src', 'ui', 'BrandTabBar.tsx'), 'utf8'));

test('primary navigation is four browsing destinations, in order', () => {
  // NUBARCA-UX-01 §5. Sync left the bar deliberately: those four are places
  // you look at, and synchronisation is a capability you configure once. It
  // took a fifth of the navigation for something most people open twice.
  const names = [...LAYOUT.matchAll(/name="(\w+)"/g)].map((m) => m[1]);
  assert.deepEqual(names, ['photos', 'videos', 'albums', 'files']);
});

test('sync is still reachable, and its engine is untouched', () => {
  const account = code(readFileSync(resolve(ROOT, 'app', 'account.tsx'), 'utf8'));
  assert.match(account, /router\.push\('\/sync'\)/);
  // The route renders the SAME screen; nothing about synchronisation moved
  // except where you find it.
  const route = code(readFileSync(resolve(ROOT, 'app', 'sync.tsx'), 'utf8'));
  assert.match(route, /import \{ SyncScreen \} from '\.\.\/src\/sync\/SyncScreen'/);
  assert.match(route, /<SyncScreen \/>/);
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

test('the bottom navigation steps aside for selection, by state', () => {
  // NUBARCA-UX-01.1 §1. The two bottom surfaces used to be able to occupy the
  // same space, with the tray rendered under the translucent bar and its
  // actions unreachable. This is fixed by RENDER, not by a stacking order
  // somebody has to keep true.
  assert.match(BAR, /const selecting = useSelectionMode\(\)/);
  assert.match(BAR, /if \(selecting\) return null;/);
  // And the galleries that can select publish that state.
  for (const screen of [['app', '(tabs)', 'photos.tsx'], ['app', 'album', '[id].tsx']]) {
    const source = code(readFileSync(resolve(ROOT, ...screen), 'utf8'));
    assert.match(
      source,
      /useReportSelectionMode\(selectionState\.selecting\)/,
      `${screen.join('/')} does not publish selection mode`,
    );
  }
});

test('leaving a screen mid-selection does not strand the navigation', () => {
  // The publisher clears on unmount as well as on change: otherwise navigating
  // away while selecting would leave the bar hidden behind a mode nobody is in.
  const mode = code(readFileSync(resolve(ROOT, 'src', 'ui', 'selectionMode.tsx'), 'utf8'));
  assert.match(mode, /return \(\) => context\?\.setSelecting\(false\)/);
});

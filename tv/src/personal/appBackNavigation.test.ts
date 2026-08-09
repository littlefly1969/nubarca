import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const app = readFileSync(new URL('../../App.tsx', import.meta.url), 'utf8');
const platform = readFileSync(new URL('../lib/tvPlatform.ts', import.meta.url), 'utf8');
const plugin = readFileSync(
  new URL('../../plugins/withTvPlatformModule.js', import.meta.url), 'utf8');

// Strip comments before asserting on CODE. Without this, a comment merely
// MENTIONING the retired call satisfies a source regex — which is how the
// previous version of this test went green while App.tsx no longer contained
// the behaviour at all.
function code(source: string): string {
  return source
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .split('\n')
    .filter((line) => !line.trimStart().startsWith('//'))
    .join('\n');
}

test('the final BACK at either TV root removes the Android task', () => {
  const source = code(app);
  assert.match(source, /flow\.name !== 'mode' && flow\.name !== 'pairing'/);
  assert.match(source, /addEventListener\('hardwareBackPress', onBackPress\)/);
  assert.match(source, /exitTvApp\(\)/);
});

test('the root BACK no longer merely backgrounds the task', () => {
  // BackHandler.exitApp() maps to Activity.moveTaskToBack(true): on a Fire
  // Stick it showed the launcher but left NubArca in the task list, and a
  // relaunch resumed the old Activity. App.tsx must not call it.
  assert.doesNotMatch(code(app), /BackHandler\.exitApp\(\)/);
});

test('the native bridge calls finishAndRemoveTask and never kills the process', () => {
  // The plugin's own prose explains why killing the process is wrong, so the
  // negative assertion has to run against the emitted Kotlin, not the file.
  const kotlin = code(plugin);
  assert.match(kotlin, /activity\.finishAndRemoveTask\(\)/);
  // Killing the process skips orderly teardown and leaves the platform holding
  // stale saved state. A cached process surviving task removal is CORRECT
  // Android behaviour, not a leak to "fix" this way.
  assert.doesNotMatch(kotlin, /System\.exit|killProcess|Runtime\.getRuntime\(\)\.exit/);
});

test('the plugin refuses to silently skip its own registration', () => {
  // Expo prebuild regenerates android/, so the plugin re-applies every time. If
  // the template moves, it must FAIL the build rather than produce an APK whose
  // final BACK does nothing — the exact defect it exists to fix.
  assert.match(plugin, /throw new Error\(/);
  assert.match(plugin, /Refusing to continue/);
});

test('the JavaScript side falls back rather than trapping the user', () => {
  // The dev client and iOS have no native module. Backgrounding is the best
  // available outcome there and is not a product requirement, so the fallback
  // is the old, weaker behaviour — never a dead BACK button.
  assert.match(code(platform), /BackHandler\.exitApp\(\)/);
  assert.match(code(platform), /NubArcaTvPlatform/);
});

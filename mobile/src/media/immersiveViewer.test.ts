// System-bar rules (device-reported: the Android bars stayed over full-screen
// media, and stayed there).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { applySystemBars, systemBarsFor } from './immersiveViewer.ts';

test('media with the chrome hidden goes immersive', () => {
  assert.equal(systemBarsFor({ viewerOpen: true, chromeVisible: false }), 'immersive');
});

test('the bars come back WITH the chrome', () => {
  // Hiding both at once leaves a screen with no visible way out.
  assert.equal(systemBarsFor({ viewerOpen: true, chromeVisible: true }), 'visible');
});

test('outside the viewer the bars are always visible', () => {
  // Whatever the chrome flag happens to hold, leaving the viewer restores them.
  assert.equal(systemBarsFor({ viewerOpen: false, chromeVisible: false }), 'visible');
  assert.equal(systemBarsFor({ viewerOpen: false, chromeVisible: true }), 'visible');
});

test('applying a mode asks for hidden or visible accordingly', async () => {
  const calls: string[] = [];
  const controller = {
    setVisibilityAsync: async (v: 'visible' | 'hidden') => { calls.push(v); },
    setBehaviorAsync: async (b: string) => { calls.push(`behavior:${b}`); },
  };
  await applySystemBars(controller, 'immersive');
  await applySystemBars(controller, 'visible');
  assert.deepEqual(calls, ['behavior:overlay-swipe', 'hidden', 'behavior:overlay-swipe', 'visible']);
});

test('the bars stay swipe-revealable, never sticky-hidden', async () => {
  // The user must be able to summon them back regardless of what the app
  // believes about its own state.
  const behaviors: string[] = [];
  await applySystemBars({
    setVisibilityAsync: async () => {},
    setBehaviorAsync: async (b: string) => { behaviors.push(b); },
  }, 'immersive');
  assert.deepEqual(behaviors, ['overlay-swipe']);
});

test('a device that refuses system-bar control does not break the viewer', async () => {
  await applySystemBars({
    setVisibilityAsync: async () => { throw new Error('unsupported'); },
  }, 'immersive');
  // A platform without a controller at all is fine too.
  await applySystemBars(null, 'immersive');
});

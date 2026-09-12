import assert from 'node:assert/strict';
import test from 'node:test';
import { shouldKeepPartyDisplayAwake } from './wakePolicy.ts';

// The party GAME's answer to "hold the screen", from the same module as every
// other answer to it. The slideshow's own policy (shouldKeepPhotoSlideshowAwake
// and expo-video's) is exercised in mediaLifecycle.test.ts and governs the
// assigned party's slideshow unchanged.

test('a live game on screen in the foreground holds the screen, and nothing else does', () => {
  for (const hostActive of [true, false]) {
    for (const showing of [true, false]) {
      for (const presentationActive of [true, false]) {
        assert.equal(
          shouldKeepPartyDisplayAwake({ hostActive, showing, presentationActive }),
          hostActive && showing && presentationActive,
          JSON.stringify({ hostActive, showing, presentationActive }));
      }
    }
  }
});

test('backgrounding always gives the lock back', () => {
  assert.equal(
    shouldKeepPartyDisplayAwake({ hostActive: false, showing: true, presentationActive: true }),
    false);
});

test('a native cover or an error card holds nothing', () => {
  // The cover is up (renderer not yet proven, or being replaced).
  assert.equal(
    shouldKeepPartyDisplayAwake({ hostActive: true, showing: false, presentationActive: true }),
    false);
  // The page is up but says no live scene is on screen (its own error card).
  assert.equal(
    shouldKeepPartyDisplayAwake({ hostActive: true, showing: true, presentationActive: false }),
    false);
});

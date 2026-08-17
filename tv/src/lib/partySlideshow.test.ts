import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  beginVideoRotation,
  DEFAULT_PHOTO_SLIDE_MS,
  onVideoEnded,
  onVideoProgress,
  photoSlideMs,
  shouldArmPreparingGrace,
  videoCapSeconds,
  VIDEO_PREPARING_GRACE_MS,
  type PartySlideshowTiming,
} from './partySlideshow.ts';

const party = (photoSeconds: number, maxVideoSeconds: number): PartySlideshowTiming =>
  ({ photoSeconds, maxVideoSeconds });

// ------------------------------------------------------------------- photos

test('a party photo uses the configured duration; a non-party photo keeps 9 s', () => {
  assert.equal(photoSlideMs(party(15, 60)), 15_000);
  assert.equal(photoSlideMs(party(3, 60)), 3_000);
  // Non-party has no server timing at all and must not change behaviour.
  assert.equal(photoSlideMs(null), DEFAULT_PHOTO_SLIDE_MS);
  assert.equal(DEFAULT_PHOTO_SLIDE_MS, 9000);
});

test('an out-of-range photo duration is clamped, never obeyed', () => {
  // A 0 s interval would strobe the wall; an hour would look like a freeze.
  // The server validates, but a stale or hostile payload must not get through.
  assert.equal(photoSlideMs(party(0, 60)), 3_000);
  assert.equal(photoSlideMs(party(-5, 60)), 3_000);
  assert.equal(photoSlideMs(party(3600, 60)), 60_000);
  assert.equal(photoSlideMs(party(Number.NaN, 60)), 3_000);
});

// ------------------------------------------------------------------- videos

test('a party video is capped; a non-party video plays to its end', () => {
  assert.equal(videoCapSeconds(party(9, 45)), 45);
  assert.equal(videoCapSeconds(null), null);
  assert.equal(videoCapSeconds(party(9, 0)), 5);      // clamped to the minimum
  assert.equal(videoCapSeconds(party(9, 99_999)), 600); // clamped to the maximum
});

test('a video ending before the cap advances exactly once', () => {
  const start = beginVideoRotation(60);
  const ended = onVideoEnded(start);
  assert.equal(ended.advance, true);
  // A duplicated playToEnd (or a cap report arriving after the end) must not
  // advance a second time and skip the following item.
  assert.equal(onVideoEnded(ended.state).advance, false);
  assert.equal(onVideoProgress(ended.state, 999).advance, false);
});

test('a video reaching the cap advances exactly once', () => {
  let state = beginVideoRotation(30);
  assert.equal(onVideoProgress(state, 10).advance, false);
  assert.equal(onVideoProgress(state, 29.9).advance, false);

  const capped = onVideoProgress(state, 30);
  assert.equal(capped.advance, true);
  state = capped.state;

  // Further progress reports keep arriving until the component unmounts.
  assert.equal(onVideoProgress(state, 31).advance, false);
  assert.equal(onVideoProgress(state, 45).advance, false);
});

test('the cap and a natural end on the same frame advance only once', () => {
  // The exact double-advance this latch exists to prevent: without it the
  // slideshow would jump two items and silently never show one.
  const state = beginVideoRotation(20);
  const capped = onVideoProgress(state, 20);
  assert.equal(capped.advance, true);
  assert.equal(onVideoEnded(capped.state).advance, false);

  const ended = onVideoEnded(beginVideoRotation(20));
  assert.equal(ended.advance, true);
  assert.equal(onVideoProgress(ended.state, 20).advance, false);
});

test('time spent paused cannot consume the cap', () => {
  // The player reports its OWN clock. A pause simply stops producing reports,
  // so the position the slideshow sees does not move — which is the whole
  // reason the cap is not a setTimeout.
  const state = beginVideoRotation(30);
  assert.equal(onVideoProgress(state, 12).advance, false);
  // …ten minutes of wall clock pass while paused; the next report resumes from
  // where playback actually was.
  assert.equal(onVideoProgress(state, 12).advance, false);
  assert.equal(onVideoProgress(state, 13).advance, false);
  // And playback continuing past the cap still advances normally.
  assert.equal(onVideoProgress(state, 30).advance, true);
});

test('seeking past the cap advances', () => {
  const state = beginVideoRotation(15);
  assert.equal(onVideoProgress(state, 3).advance, false);
  // The user jumps forward with the remote beyond the cap.
  assert.equal(onVideoProgress(state, 90).advance, true);
});

test('an uncapped video never advances on progress', () => {
  const state = beginVideoRotation(null);
  for (const t of [0, 10, 600, 100_000]) {
    assert.equal(onVideoProgress(state, t).advance, false);
  }
  // It still advances at its natural end — that is the non-party contract.
  assert.equal(onVideoEnded(state).advance, true);
});

// --------------------------------------------------------------- preparing

test('only an autoplaying party slideshow skips an unplayable video', () => {
  const base = { partyEnabled: true, playing: true, isVideo: true, videoReady: false };
  assert.equal(shouldArmPreparingGrace(base), true);

  // Paused: the user is looking at it deliberately, so nothing is skipped.
  assert.equal(shouldArmPreparingGrace({ ...base, playing: false }), false);
  // Not a party: manual viewer behaviour is untouched by this slice.
  assert.equal(shouldArmPreparingGrace({ ...base, partyEnabled: false }), false);
  // Already playable: nothing to wait for.
  assert.equal(shouldArmPreparingGrace({ ...base, videoReady: true }), false);
  // A photo has its own timer.
  assert.equal(shouldArmPreparingGrace({ ...base, isVideo: false }), false);
});

test('the preparing grace is ten seconds', () => {
  assert.equal(VIDEO_PREPARING_GRACE_MS, 10_000);
});

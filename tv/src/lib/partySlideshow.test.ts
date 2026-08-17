import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import {
  beginVideoRotation,
  DEFAULT_PHOTO_SLIDE_MS,
  onVideoEnded,
  onVideoProgress,
  photoRotationActive,
  photoSlideMs,
  resolvePlayPause,
  shouldArmPreparingGrace,
  videoCapSeconds,
  videoPlaybackProps,
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

test('only a ROTATING party slideshow skips an unplayable video', () => {
  const base = {
    slideshowMode: true, partyEnabled: true, playing: true,
    isVideo: true, videoReady: false,
  };
  assert.equal(shouldArmPreparingGrace(base), true);

  // Paused: the user is looking at it deliberately, so nothing is skipped.
  assert.equal(shouldArmPreparingGrace({ ...base, playing: false }), false);
  // Opened from the grid to watch: there is no rotation to protect, so a slow
  // transcode must never yank the video out from under the viewer.
  assert.equal(shouldArmPreparingGrace({ ...base, slideshowMode: false }), false);
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

// ------------------------------------------------- playback authority (fix B)

test('in a slideshow the play key drives the SLIDESHOW, video or not', () => {
  // The defect: while a video was current the key went straight to the player,
  // so the video paused while the slideshow still believed it was playing.
  assert.equal(
    resolvePlayPause({ slideshowMode: true, isVideo: true }), 'toggle-slideshow');
  assert.equal(
    resolvePlayPause({ slideshowMode: true, isVideo: false }), 'toggle-slideshow');
});

test('a manually opened video keeps direct player control', () => {
  assert.equal(
    resolvePlayPause({ slideshowMode: false, isVideo: true }), 'toggle-video-player');
});

test('the play key still promotes a manual PHOTO into a slideshow', () => {
  // Historical behaviour of the photo viewer, kept deliberately and made an
  // explicit transition rather than a side effect of `playing` turning true.
  assert.equal(
    resolvePlayPause({ slideshowMode: false, isVideo: false }), 'promote-to-slideshow');
});

test('pausing a slideshow stops the photo rotation, so the next photo waits', () => {
  // Requirement 3: after pausing on a video, moving to a photo must NOT
  // auto-advance — which is only true if one state drives both.
  assert.equal(photoRotationActive({ slideshowMode: true, playing: true }), true);
  assert.equal(photoRotationActive({ slideshowMode: true, playing: false }), false);
  // A manually opened photo never rotates on its own.
  assert.equal(photoRotationActive({ slideshowMode: false, playing: false }), false);
  assert.equal(photoRotationActive({ slideshowMode: false, playing: true }), false);
});

test('a slideshow controls the player; a manual view does not', () => {
  const timing = party(9, 45);

  // Slideshow: the player is CONTROLLED, so pause reaches it and the pill and
  // the audio cannot disagree.
  const playingShow = videoPlaybackProps({
    slideshowMode: true, partyEnabled: true, playing: true, timing,
  });
  assert.equal(playingShow.playing, true);
  assert.equal(playingShow.maxPlaybackSeconds, 45);

  const pausedShow = videoPlaybackProps({
    slideshowMode: true, partyEnabled: true, playing: false, timing,
  });
  assert.equal(pausedShow.playing, false);
  // Requirement 4: pausing does not change the cap, so resuming continues
  // against the same media-time budget (the latch is untouched by this).
  assert.equal(pausedShow.maxPlaybackSeconds, 45);
});

test('a party video opened from the grid is NOT capped by the slideshow', () => {
  // Requirement 5. PartyMaxVideoSlideSeconds bounds how long a video may hold
  // the ROTATION; someone who picked that video to watch is not holding
  // anything, and cutting them off at 45 s would be wrong.
  const manual = videoPlaybackProps({
    slideshowMode: false, partyEnabled: true, playing: false, timing: party(9, 45),
  });
  assert.equal(manual.maxPlaybackSeconds, null);
  assert.equal(manual.playing, undefined, 'the player keeps its own controls');
});

test('a party video reached through the slideshow IS capped', () => {
  // Requirement 6 — the same video, the other way in.
  const viaSlideshow = videoPlaybackProps({
    slideshowMode: true, partyEnabled: true, playing: true, timing: party(9, 45),
  });
  assert.equal(viaSlideshow.maxPlaybackSeconds, 45);
});

test('a non-party slideshow is never capped and keeps the 9 s photo timing', () => {
  // Requirement 7: nothing party-specific leaks into an ordinary TV album.
  const nonParty = videoPlaybackProps({
    slideshowMode: true, partyEnabled: false, playing: true, timing: null,
  });
  assert.equal(nonParty.maxPlaybackSeconds, null);
  assert.equal(photoSlideMs(null), DEFAULT_PHOTO_SLIDE_MS);
  assert.equal(shouldArmPreparingGrace({
    slideshowMode: true, partyEnabled: false, playing: true,
    isVideo: true, videoReady: false,
  }), false);
});

// The policy above is only worth anything if the screen actually defers to it.
// This is the same source-level guard mediaMenuFocus.test.ts already uses for
// the focus contract.
test('ViewerScreen routes play/pause and player props through the policy', () => {
  const source = readFileSync(new URL('../screens/ViewerScreen.tsx', import.meta.url), 'utf8');

  assert.match(source, /resolvePlayPause\(/,
    'the play key must go through the single-authority decision');
  assert.match(source, /videoPlaybackProps\(/,
    'cap and controlled play state must come from the policy');
  assert.match(source, /photoRotationActive\(/,
    'the photo timer must key off the session, not `playing` alone');

  // The exact defect: an unconditional direct call to the player on the
  // play/pause key, bypassing the slideshow's state.
  assert.doesNotMatch(
    source,
    /if \(isVideo && videoControlsRef\.current\) videoControlsRef\.current\.togglePlay\(\)/,
    'play/pause must not talk to the player directly while a slideshow is running');
  // And the cap must not be derived from partyEnabled alone any more.
  assert.doesNotMatch(
    source,
    /maxPlaybackSeconds=\{partyEnabled \?/,
    'the slideshow cap must not be applied to a manually opened video');
});

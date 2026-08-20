import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { shouldKeepPhotoSlideshowAwake, shouldRotateSlideshow } from './wakePolicy.ts';
import {
  pausesOnOutputLoss, releasesPlayer, restorablePosition, resumesFromBackground,
  resumesOnOutputRestored, shouldAutoResume, shouldMountPlayer,
} from './playerLifecycle.ts';

const read = (p: string) => readFileSync(new URL(p, import.meta.url), 'utf8');
const code = (s: string) => s.replace(/\/\*[\s\S]*?\*\//g, '')
  .split('\n').filter((l) => !l.trimStart().startsWith('//')).join('\n');

const player = code(read('../components/TvVideoPlayer.tsx'));
const partyViewer = code(read('../screens/ViewerScreen.tsx'));
const personalViewer = code(read('../screens/library/PersonalMediaViewer.tsx'));

// ------------------------------------------------------------------ wake

const wake = (kind: 'photo' | 'video', slideshowPlaying: boolean, hostActive = true) =>
  shouldKeepPhotoSlideshowAwake({ kind, slideshowPlaying, hostActive });

test('a still photograph does not keep the television awake', () => {
  // The defect this replaces: `useScreenAwake(true)` for the viewer's whole
  // lifetime kept a panel lit because a picture was open, defeating the
  // platform's own ambient behaviour.
  assert.equal(wake('photo', false), false);
});

test('an actively rotating slideshow does keep it awake', () => {
  assert.equal(wake('photo', true), true);
});

test('a paused slideshow releases the lock', () => {
  assert.equal(wake('photo', false), false);
});

test('background releases the lock whatever the slideshow was doing', () => {
  assert.equal(wake('photo', true, false), false);
  assert.equal(wake('photo', false, false), false);
});

test('NubArca never holds a video keep-awake — expo-video owns that', () => {
  // Two authorities for one lock is how one of them gets stuck holding it.
  for (const playing of [true, false]) {
    for (const hostActive of [true, false]) {
      assert.equal(wake('video', playing, hostActive), false);
    }
  }
});

test('rotation and the wake lock are the same decision', () => {
  // A slideshow advancing behind HOME does work nobody can see and drags the
  // lock with it, so the two answers are one function.
  for (const kind of ['photo', 'video'] as const) {
    for (const playing of [true, false]) {
      for (const hostActive of [true, false]) {
        const inputs = { kind, slideshowPlaying: playing, hostActive };
        assert.equal(shouldRotateSlideshow(inputs), shouldKeepPhotoSlideshowAwake(inputs));
      }
    }
  }
});

test('neither viewer holds an unconditional wake lock any more', () => {
  for (const [name, source] of Object.entries({ partyViewer, personalViewer })) {
    assert.doesNotMatch(source, /useScreenAwake\(true\)/,
      `${name} still keeps the screen awake for its whole lifetime`);
    assert.match(source, /useScreenAwake\(shouldKeepPhotoSlideshowAwake\(/, name);
  }
});

test('the video keep-awake invariant is explicit in the player', () => {
  // Set even though Expo currently defaults it true, so the product invariant
  // is visible and a default change cannot silently remove it.
  assert.match(player, /p\.keepScreenOnWhilePlaying = true;/);
});

// -------------------------------------------------------------- lifecycle

test('a real background transition releases the player', () => {
  assert.equal(releasesPlayer('active', 'background'), true);
  assert.equal(shouldMountPlayer('background'), false);
});

test('a transient inactive blur does NOT release the player', () => {
  // Tearing ExoPlayer down for an incidental focus change would churn the
  // decoder and show a black frame for nothing.
  assert.equal(releasesPlayer('active', 'inactive'), false);
  assert.equal(shouldMountPlayer('inactive'), true);
  assert.equal(shouldMountPlayer('active'), true);
});

test('background twice does not re-release', () => {
  assert.equal(releasesPlayer('background', 'background'), false);
});

test('returning from background is recognised, from inactive is not', () => {
  assert.equal(resumesFromBackground('background', 'active'), true);
  assert.equal(resumesFromBackground('inactive', 'active'), false);
});

test('a position is restored only for the SAME source', () => {
  const snapshot = { source: '/api/tv/personal/media/a/video', positionSeconds: 42, wasPlaying: true };
  assert.equal(restorablePosition(snapshot, '/api/tv/personal/media/a/video'), 42);
  // Changing item while backgrounded must never seek the new video to the old
  // one's timestamp.
  assert.equal(restorablePosition(snapshot, '/api/tv/personal/media/b/video'), null);
  assert.equal(restorablePosition(null, '/api/tv/personal/media/a/video'), null);
});

test('a nonsensical snapshot is discarded rather than applied', () => {
  const bad = (positionSeconds: number) =>
    restorablePosition({ source: 's', positionSeconds, wasPlaying: true }, 's');
  assert.equal(bad(-1), null);
  assert.equal(bad(Number.NaN), null);
  assert.equal(bad(Number.POSITIVE_INFINITY), null);
  assert.equal(bad(0), 0, 'the very start is a legitimate position');
});

test('playback never resumes by itself', () => {
  // Returning to a room and having audio start on its own is the behaviour this
  // product avoids — even when the user WAS playing before the interruption.
  assert.equal(shouldAutoResume(), false);
  assert.equal(resumesOnOutputRestored(), false);
});

test('losing the audio output pauses, and only when something is playing', () => {
  assert.equal(pausesOnOutputLoss(true), true);
  assert.equal(pausesOnOutputLoss(false), false);
});

test('the player snapshots before it is unmounted', () => {
  // The position has to be pulled from OUTSIDE, because the component that owns
  // it is the one being torn down.
  assert.match(player, /snapshot\(\): PlaybackSnapshot \| null;/);
  assert.match(player, /snapshotRef\.current = controlsForSnapshot\.current\?\.snapshot\(\)/);
  const snapshotAt = player.indexOf('snapshotRef.current = controlsForSnapshot');
  const setHostAt = player.indexOf('setHost(state)');
  assert.ok(snapshotAt > 0 && snapshotAt < setHostAt,
    'the snapshot must be taken before the state change that unmounts the player');
});

test('the mount gate is what releases the native player', () => {
  assert.match(player, /shouldMountPlayer\(host\)/);
  assert.match(player, /restorablePosition\(restoreSnapshot, videoPath\)/);
});

// ------------------------------------------------------- single-player rules

test('there is exactly one player and no second owner', () => {
  // useVideoPlayer's release-on-unmount IS the design; createVideoPlayer would
  // hand NubArca a lifetime it does not want to manage.
  assert.doesNotMatch(player, /createVideoPlayer/);
  assert.equal([...player.matchAll(/useVideoPlayer\(/g)].length, 1);
  assert.match(player, /key=\{videoPath\}/, 'a new source must be a new component instance');
});

test('NubArca adds no MediaSession and no audio-focus owner', () => {
  // expo-video already owns both. A second one is the double-dispatch and the
  // duplicate registration the audit exists to prevent.
  const nativeSources = [
    read('../../plugins/withTvPlatformModule.js'),
    player, partyViewer, personalViewer,
  ].join('\n');
  for (const forbidden of [
    /androidx\.media3\.session\.MediaSession/,
    /MediaSession\.Builder/,
    /OnAudioFocusChangeListener/,
    /requestAudioFocus/,
    /abandonAudioFocus/,
  ]) {
    assert.doesNotMatch(nativeSources, forbidden,
      `NubArca must not own this: ${forbidden}`);
  }
});

test('background playback is not enabled', () => {
  // NubArca is an activity-based foreground video app; enabling either of these
  // would start Expo's playback service and keep a session alive behind HOME.
  assert.doesNotMatch(player, /staysActiveInBackground\s*=\s*true/);
  assert.doesNotMatch(player, /showNowPlayingNotification\s*=\s*true/);
});

test('exactly one AppState authority owns the player lifecycle', () => {
  assert.equal([...player.matchAll(/AppState\.addEventListener/g)].length, 1);
});

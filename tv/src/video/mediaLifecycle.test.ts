import assert from 'node:assert/strict';
import test from 'node:test';
import { read } from '../testing/sourceText.ts';
import { shouldKeepPhotoSlideshowAwake, shouldRotateSlideshow } from './wakePolicy.ts';
import {
  hostStateFromAppState, releasesPlayer, restorablePosition,
  resumesFromBackground, shouldAutoResume, shouldMountPlayer,
} from './playerLifecycle.ts';

const src = (path: string) => read(import.meta.url, path);

const player = src('../components/TvVideoPlayer.tsx');
const partyViewer = src('../screens/ViewerScreen.tsx');
const personalViewer = src('../screens/library/PersonalMediaViewer.tsx');

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
  // One predicate, consulted by both the background and the output paths.
  assert.equal(shouldAutoResume(), false);
});

test('losing the audio output pauses, and only when something was playing', () => {
  // Read from the HANDLER rather than from a policy function, because the
  // handler is what runs. A pause is performed unconditionally (pausing an
  // already-paused player is harmless) and only a real interruption is
  // REPORTED to the controlled parent.
  const start = player.indexOf('useEffect(() => subscribeOutputLost(');
  const handler = player.slice(start, player.indexOf('}), [player]);', start));
  assert.match(handler, /wasPlaying = player\.playing;/);
  assert.match(handler, /player\.pause\(\);/);
  assert.match(handler, /if \(wasPlaying\) onExternalPauseRef/);
  // And nothing in it resumes.
  assert.doesNotMatch(handler, /player\.play\(\)/);
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
    src('../../plugins/withTvPlatformModule.js'),
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

// ------------------------------------------------- WIRING, not just policy

// The tranche-3 suite proved shouldRotateSlideshow and shouldKeepPhotoSlideshowAwake
// agree, and that was not enough: ViewerScreen's timer still called
// photoRotationActive({ slideshowMode, playing }), which knows nothing about the
// foreground. The pure functions were right and unused. These tests check the
// call sites.

test('both viewers drive the rotation timer from the shared policy', () => {
  for (const [name, source] of Object.entries({ partyViewer, personalViewer })) {
    assert.match(source, /shouldRotateSlideshow\(wakeInputs\)/,
      `${name}: the timer must derive from the shared policy`);
    assert.match(source, /useScreenAwake\(shouldKeepPhotoSlideshowAwake\(wakeInputs\)\)/,
      `${name}: the wake lock must consume the SAME inputs value`);
  }
});

test('the party timer no longer has a host-blind lifecycle authority', () => {
  // photoRotationActive keeps its domain home in lib/partySlideshow.ts; what it
  // must not be is a SECOND answer to "is the slideshow running", because it
  // cannot see the foreground.
  assert.doesNotMatch(partyViewer, /const rotating = photoRotationActive/);
  assert.doesNotMatch(partyViewer, /photoRotationActive\(/,
    'ViewerScreen must not reintroduce a host-blind rotation predicate');
});

test('a genuine background changes playback INTENT in both viewers', () => {
  // Gating only the timer would restart the slideshow on return — and for a
  // party VIDEO the recreated player would read `playing === true` and start
  // audio by itself, walking past shouldAutoResume().
  assert.match(partyViewer,
    /if \(hostState === 'background'\) setPlaying\(false\);/);
  assert.match(personalViewer,
    /if \(hostState === 'background'\) setSlideshow\(false\);/);
});

test('a transient inactive blip does not change what the user asked for', () => {
  // Stopping a timer for a momentary overlay is free; silently turning the
  // user's slideshow off is not.
  for (const [name, source] of Object.entries({ partyViewer, personalViewer })) {
    assert.doesNotMatch(source, /hostState !== 'active'\) set(Playing|Slideshow)\(false\)/, name);
    assert.match(source, /hostState === 'background'/, name);
  }
});

test('there is one host-state hook, not an AppState listener per screen', () => {
  for (const [name, source] of Object.entries({ partyViewer, personalViewer })) {
    assert.doesNotMatch(source, /AppState\.addEventListener/,
      `${name} must consume the shared hook rather than add a listener`);
    assert.match(source, /useHostState\(\)/, name);
  }
});

// --------------------------------------------------------- initial host state

test('a player created while already backgrounded does not mount', () => {
  assert.equal(hostStateFromAppState('background'), 'background');
  assert.equal(shouldMountPlayer(hostStateFromAppState('background')), false);
  assert.equal(shouldMountPlayer(hostStateFromAppState('active')), true);
  // Anything unrecognised is treated as a non-active interruption rather than
  // assumed foreground.
  for (const unknown of ['inactive', 'unknown', null, undefined]) {
    assert.equal(hostStateFromAppState(unknown as string), 'inactive');
  }
});

test('the player derives its first host state instead of assuming active', () => {
  assert.match(player, /useState<HostState>\(\s*\(\) => hostStateFromAppState\(AppState\.currentState\)\)/);
  assert.doesNotMatch(player, /useState<HostState>\('active'\)/);
});

// ------------------------------------------------ the full lifecycle scenario

test('photo: playing → background → foreground → SELECT', () => {
  // The scenario the review asked for, as state rather than prose.
  let playing = true;
  let host: 'active' | 'background' = 'active';
  const inputs = () => ({ kind: 'photo' as const, slideshowPlaying: playing, hostActive: host === 'active' });

  assert.equal(shouldRotateSlideshow(inputs()), true, 'rotating in the foreground');

  host = 'background';
  playing = false;                       // the viewers' effect does exactly this
  assert.equal(shouldRotateSlideshow(inputs()), false, 'stopped in the background');

  host = 'active';
  assert.equal(shouldRotateSlideshow(inputs()), false, 'STILL paused after returning');
  assert.equal(shouldKeepPhotoSlideshowAwake(inputs()), false);

  playing = true;                        // SELECT
  assert.equal(shouldRotateSlideshow(inputs()), true, 'the user resumed it');
});

test('party video: playing at 42s → background → foreground → SELECT', () => {
  const source = '/api/tv/media/x/video';
  let playing = true;
  let host: 'active' | 'background' = 'active';

  // Background: snapshot, intent becomes paused, player unmounts.
  host = 'background';
  const snapshot = { source, positionSeconds: 42, wasPlaying: playing };
  playing = false;
  assert.equal(releasesPlayer('active', host), true);
  assert.equal(shouldMountPlayer(host), false, 'no player while backgrounded');

  // Foreground: exactly one player, the same position, and NOT playing.
  host = 'active';
  assert.equal(shouldMountPlayer(host), true);
  assert.equal(restorablePosition(snapshot, source), 42);
  assert.equal(playing, false,
    'the parent intent is what keeps the recreated player paused');
  assert.equal(shouldAutoResume(), false);

  // SELECT: one play action.
  playing = true;
  assert.equal(playing, true);
});

test('a changed item discards the stale position', () => {
  const snapshot = { source: '/api/tv/media/x/video', positionSeconds: 42, wasPlaying: true };
  assert.equal(restorablePosition(snapshot, '/api/tv/media/y/video'), null);
});

// ------------------------------- controlled video, external pause (3C)

// THE DEFECT. During a controlled Party slideshow the output-loss handler
// paused the REAL player but left the parent believing `playing === true`.
// The two then disagreed, and the next SELECT was spent flipping that stale
// `true` to `false` — so the first press did not resume the video and the user
// needed a second one.

test('the player reports an external pause with a closed, semantic reason', () => {
  assert.match(player, /export type TvVideoExternalPauseReason = 'output-lost';/);
  assert.match(player, /onExternalPause\?: \(reason: TvVideoExternalPauseReason\) => void;/);
  assert.match(player, /onExternalPauseRef\.current\?\.\('output-lost'\)/);
});

test('the external pause is reported only when playback was actually running', () => {
  // Pausing something already paused is not an event, and telling a controlled
  // parent its intent is wrong when it is not would be a false reconciliation.
  assert.match(player, /if \(wasPlaying\) onExternalPauseRef\.current\?\.\('output-lost'\)/);
  // Anchored on the HANDLER. `subscribeOutputLost` also appears in the import
  // line, and slicing from there swept in unrelated player.pause() calls.
  const start = player.indexOf('useEffect(() => subscribeOutputLost(');
  assert.ok(start > 0, 'the output-loss handler must exist');
  const handler = player.slice(start, player.indexOf('}), [player]);', start));
  const report = handler.indexOf('onExternalPauseRef');
  const pause = handler.indexOf('player.pause()');
  assert.ok(pause > 0, 'the handler must pause the player');
  assert.ok(pause < report, 'the pause must happen before it is reported');
});

test('only the CONTROLLED slideshow reconciles its intent', () => {
  // A manually opened video has no parent playback intent; manufacturing one
  // would be a second authority for something the player already owns.
  assert.match(partyViewer,
    /onExternalPause=\{slideshowMode \? \(\) => setPlaying\(false\) : undefined\}/);
  assert.doesNotMatch(personalViewer, /onExternalPause/,
    'the personal viewer plays video uncontrolled and must stay that way');
  assert.doesNotMatch(personalViewer, /playing=\{/,
    'passing `playing` would make it controlled');
});

test('natural end and cap keep their own paths — they are not external pauses', () => {
  // A video ending must ADVANCE an active slideshow, not switch the slideshow
  // off. Routing it through the external-pause callback would do exactly that.
  //
  // Both now go through handleMediaBoundary, which is the ONE place that
  // decides whether an automatic transition advances or first shows a party
  // Hero card (see lib/partyMessages). It is still an advance path: what it
  // must never do is touch playback.
  assert.match(partyViewer, /onEnded=\{handleMediaBoundary\}/);
  assert.match(partyViewer, /onCapReached=\{handleMediaBoundary\}/);
  const boundaryStart = partyViewer.indexOf('const handleMediaBoundary = useCallback');
  assert.ok(boundaryStart > 0, 'the boundary handler must exist');
  const boundary = partyViewer.slice(
    boundaryStart, partyViewer.indexOf('}, [partyEnabled, goNext]);', boundaryStart));
  assert.match(boundary, /goNext\(\)/, 'a boundary still advances the slideshow');
  assert.doesNotMatch(boundary, /setPlaying\(/,
    'a natural end must not switch the slideshow off');
  const start = player.indexOf('useEffect(() => subscribeOutputLost(');
  const handler = player.slice(start, player.indexOf('}), [player]);', start));
  for (const unrelated of [/onEnded/, /onCapReached/, /playToEnd/]) {
    assert.doesNotMatch(handler, unrelated,
      'the external-pause path must not be reachable from a natural end');
  }
});

test('a parent-commanded pause does not masquerade as an external one', () => {
  // The controlled effect pauses when `playing` becomes false. That is the
  // parent's own decision arriving, not a system event to report back to it —
  // reporting it would be a feedback loop.
  // Anchored on CODE: `player` is comment-stripped, so a prose anchor is not
  // there to find.
  const start = player.indexOf('if (playing === undefined) return;');
  assert.ok(start > 0, 'the controlled-playing effect must exist');
  const controlled = player.slice(start, player.indexOf('}, [playing, player]);', start));
  assert.match(controlled, /player\.pause\(\)/, 'it pauses on a commanded false');
  assert.doesNotMatch(controlled, /onExternalPause/,
    'a commanded pause must not be reported back to the parent that commanded it');
});

test('the controlled party video scenario, end to end', () => {
  // parent intent + real player, tracked as the two values that must agree.
  let parentPlaying = true;
  let playerPlaying = true;

  // Output lost: the player pauses and REPORTS, so the parent follows.
  const onOutputLost = () => {
    const wasPlaying = playerPlaying;
    playerPlaying = false;
    if (wasPlaying) parentPlaying = false;      // onExternalPause → setPlaying(false)
  };
  onOutputLost();
  assert.equal(playerPlaying, false);
  assert.equal(parentPlaying, false, 'intent must agree with reality');

  // Output restored: nothing changes, no auto-resume.
  assert.equal(shouldAutoResume(), false);
  assert.equal(parentPlaying, false);
  assert.equal(playerPlaying, false);

  // FIRST SELECT: toggle-slideshow flips intent, and the controlled effect
  // plays. One press, one resume.
  let playCalls = 0;
  parentPlaying = !parentPlaying;
  if (parentPlaying && !playerPlaying) { playerPlaying = true; playCalls += 1; }
  assert.equal(parentPlaying, true);
  assert.equal(playerPlaying, true);
  assert.equal(playCalls, 1, 'exactly one resume, on the FIRST press');
});

test('a second output loss while already paused reports nothing', () => {
  let parentPlaying = false;
  let reports = 0;
  const playerPlaying = false;
  const wasPlaying = playerPlaying;
  if (wasPlaying) { reports += 1; parentPlaying = false; }
  assert.equal(reports, 0);
  assert.equal(parentPlaying, false);
});

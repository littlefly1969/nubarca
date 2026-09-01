// VideoSlide probe-lifecycle WIRING contract (MOBILE-VIDEO-PROBE-LIFECYCLE-01).
//
// The mobile harness has NO component renderer (plain `node --test`, no
// react-test-renderer dependency), so this file pins the wiring the pieces
// MUST have — one managed (cancellable) probe per effect, cleanup aborts it,
// stale verdicts never reach state, and the managed controller's SIGNAL is
// what actually rides every attempt — while the BEHAVIOUR of that mechanism
// is proven against the real probe in videoProbe.test.ts (hung-attempt
// timeout, caller-abort, retry-delay-abort and managed-cancel regressions).

import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  playerStatusFor,
  refreshVideoSourceCookie,
  shouldPlayVideo,
  snapshotPlayerStatus,
  videoPresentation,
} from './videoPlayback.ts';

const sourceCache = new Map<string, string>();

async function sourceOf(relativePath: string): Promise<string> {
  const cached = sourceCache.get(relativePath);
  if (cached !== undefined) return cached;
  const here = dirname(fileURLToPath(import.meta.url));
  const text = await readFile(join(here, relativePath), 'utf8');
  sourceCache.set(relativePath, text);
  return text;
}

test('the probe effect runs ONE MANAGED probe, never a raw unmanaged one', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  // The controller lives inside createManagedProbe (media/videoProbe.ts); the
  // slide must go through the cancellable wrapper.
  assert.match(slide, /createManagedProbe\(/);
  assert.doesNotMatch(slide, /probeVideoSource\(/);
});

test('effect cleanup ABORTS the probe (real cancellation, not just ignoring)', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  assert.match(slide, /probe\.cancel\(\)/);
});

test('the managed controller signal rides EVERY attempt in videoProbe', async () => {
  const probe = await sourceOf('../media/videoProbe.ts');
  assert.match(probe, /signal:\s*controller\.signal/);
});

test('stale verdicts never reach state: the cancelled guard precedes setProbeState', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  const guard = slide.indexOf('if (cancelled) return;');
  const applyOutcome = slide.indexOf('setProbeState(next)');
  assert.ok(guard !== -1, 'defence-in-depth cancelled guard missing');
  assert.ok(applyOutcome !== -1, 'outcome application missing');
  assert.ok(
    guard < applyOutcome,
    'outcome may only be applied AFTER the cancelled guard',
  );
  // A cancelled probe maps to null and is dropped before it can paint an
  // error over a video that was never actually unavailable.
  assert.match(slide, /const next = probeStateForOutcome\(outcome\);\s*\n\s*if \(next === null\) return;/);
});

test('the slide carries NO probe policy of its own: no attempt ceiling, no local backoff', async () => {
  // VIDEO-DELIVERY-PARITY-01: the retry policy is the shared one
  // (media/videoDelivery.ts). A mobile-only ceiling here is what turned a long
  // but healthy transcode into an error after ten attempts.
  const slide = await sourceOf('VideoSlide.tsx');
  assert.doesNotMatch(slide, /maxAttempts/);
  assert.doesNotMatch(slide, /retryMs/);
  assert.doesNotMatch(slide, /VIDEO_PROBE_MAX_ATTEMPTS/);
});

test('the expo-video source is built by the shared resolver, contentType included', async () => {
  // Both containers declare contentType, and the played URL is the probed one
  // — a shared album must never be rewritten to an owner route.
  const slide = await sourceOf('VideoSlide.tsx');
  assert.match(slide, /resolveExpoVideoSource\(source, \{ kind: 'ready', mode: resolvedContainer \}\)/);
  assert.doesNotMatch(slide, /contentType: 'hls' as const/);
});

test('an already-ready player snapshot is authoritative before any event arrives', () => {
  const player = { status: 'readyToPlay' as const };
  const snapshot = snapshotPlayerStatus(player);
  assert.equal(playerStatusFor(snapshot, player), 'readyToPlay');
  assert.equal(shouldPlayVideo(true, true, snapshot.status), true);
});

test('a replacement player never inherits the previous native status', () => {
  const oldPlayer = { status: 'readyToPlay' as const };
  const nextPlayer = { status: 'loading' as const };
  const oldSnapshot = snapshotPlayerStatus(oldPlayer);
  assert.equal(playerStatusFor(oldSnapshot, nextPlayer), 'loading');
});

test('reactivating a slide refreshes only its cookie and preserves its authorized URL', () => {
  const source = {
    uri: 'https://unit.test/api/shared-albums/a/media/f/video',
    headers: { cookie: 'NubArca.Auth=old' },
  };
  const refreshed = refreshVideoSourceCookie(source, 'NubArca.Auth=new');
  assert.ok(refreshed !== null);
  assert.equal(refreshed.uri, source.uri);
  assert.equal(refreshed.headers.cookie, 'NubArca.Auth=new');
  assert.equal(refreshVideoSourceCookie(source, null), null);
});

test('playback requires active ownership, a source, and native readyToPlay', () => {
  assert.equal(shouldPlayVideo(true, true, 'readyToPlay'), true);
  assert.equal(shouldPlayVideo(false, true, 'readyToPlay'), false);
  assert.equal(shouldPlayVideo(true, false, 'readyToPlay'), false);
  assert.equal(shouldPlayVideo(true, true, 'loading'), false);
  assert.equal(shouldPlayVideo(true, true, 'error'), false);
});

test('VideoView presentation is explicit for every probe and native state', () => {
  assert.equal(videoPresentation(false, 'unavailable', false, 'idle'), 'unavailable');
  assert.equal(videoPresentation(true, 'idle', false, 'idle'), 'loading');
  assert.equal(videoPresentation(true, 'probing', false, 'idle'), 'probing');
  assert.equal(videoPresentation(true, 'preparing', false, 'idle'), 'preparing');
  assert.equal(videoPresentation(true, 'unavailable', false, 'idle'), 'unavailable');
  assert.equal(videoPresentation(true, 'error', false, 'idle'), 'error');
  assert.equal(videoPresentation(true, 'ready', true, 'idle'), 'loading');
  assert.equal(videoPresentation(true, 'ready', true, 'loading'), 'loading');
  assert.equal(videoPresentation(true, 'ready', true, 'readyToPlay'), 'ready');
  assert.equal(videoPresentation(true, 'ready', true, 'error'), 'error');
});

test('VideoSlide seeds status now, tracks events, pauses inactive, and removes old listeners', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  assert.doesNotMatch(slide, /const \[ready, setReady\] = useState\(false\)/);
  assert.match(slide, /useState\(\(\) => snapshotPlayerStatus\(player\)\)/);
  assert.match(slide, /setPlayerStatus\(snapshotPlayerStatus\(player, status\.status\)\)/);
  assert.match(slide, /if \(!active\)[\s\S]*?player\.pause\(\)/);
  assert.match(slide, /shouldPlayVideo\(active, expoSource !== null, nativeStatus\)/);

  // Keyed on the player AND its uri: the uri is what the cleanup records the
  // position under, so it must not be captured stale.
  const statusEffect = slide.match(
    /useEffect\(\(\) => \{([\s\S]*?statusSub\.remove\(\)[\s\S]*?playingSub\.remove\(\)[\s\S]*?)\n  \}, \[player, uri\]\);/,
  );
  assert.ok(statusEffect, 'player-owned listener effect with cleanup not found');
});

test('inactive virtualized neighbours do not probe and transient failures expose retry', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  assert.match(slide, /if \(!active\) return;/);
  assert.match(slide, /<ErrorState[\s\S]*?onRetry=/);
  assert.match(slide, /refreshVideoSourceCookie\(/);
});

test('VideoView is mounted only for the ready presentation', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  const loadingGuard = slide.indexOf("if (presentation !== 'ready')");
  const videoView = slide.indexOf('<VideoView');
  assert.ok(loadingGuard !== -1, 'non-ready presentation guard missing');
  assert.ok(videoView !== -1, 'VideoView missing');
  assert.ok(loadingGuard < videoView, 'VideoView may only occur after the non-ready guard');
});

test('a remount resumes where it was, and never onto another video', async () => {
  // Device-reported: rotating the phone restarted the video from zero. The
  // pager re-measures and remounts its cells, so the new player knows nothing
  // — the position has to be kept somewhere that outlives the slide.
  const slide = await sourceOf('VideoSlide.tsx');
  assert.match(slide, /rememberPosition\(uri, player\.currentTime\)/);
  assert.match(slide, /restorablePosition\(/);
  assert.match(slide, /recallPosition\(expoSource\.uri\)/);
});

test('the position is recorded BEFORE the player is paused or released', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  const remember = slide.indexOf('rememberPosition(uri, player.currentTime)');
  const pause = slide.indexOf('player.pause();', remember);
  assert.ok(remember !== -1 && pause !== -1);
  assert.ok(remember < pause, 'the position must be read before teardown');
});

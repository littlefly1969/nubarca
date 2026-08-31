// CROSS-CONSUMER PARITY (VIDEO-DELIVERY-PARITY-01) — the TV column.
//
// frontend, mobile and tv each carry a byte-identical copy of the canonical
// /video contract and each run THIS matrix through it. Two things are asserted
// here, and the same two are asserted by
// frontend/src/video/videoDeliveryParity.test.ts and
// mobile/src/media/videoDeliveryParity.test.ts:
//
//   1. the local copy of videoDelivery.ts has not drifted from
//      shared/video-delivery/videoDelivery.ts;
//   2. every row of shared/video-delivery/parity-matrix.json classifies to the
//      same verdict here as it does there, and this consumer's own adapter
//      maps each verdict kind to exactly one presentation.
//
// A rule that changes in one client therefore fails in all three at once.

import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { read as readSource } from '../testing/sourceText.ts';
import {
  INITIAL_POLL_STATE,
  TRANSIENT_MAX_RETRIES,
  classifyVideoDelivery,
  isHlsMime,
  nextPreparationDelayMs,
  planNextProbe,
  transportFailureVerdict,
  type VideoDeliveryPollState,
  type VideoDeliveryVerdictKind,
} from './videoDelivery.ts';
import { classifyVideoProbe, tvVideoModeFor, type TvVideoMode } from './probeClassify.ts';

const HERE = dirname(fileURLToPath(import.meta.url)); // tv/src/video
const ROOT = resolve(HERE, '../../..');
const CANONICAL = resolve(ROOT, 'shared/video-delivery/videoDelivery.ts');
const MATRIX = resolve(ROOT, 'shared/video-delivery/parity-matrix.json');
const LOCAL_COPY = resolve(HERE, 'videoDelivery.ts');

interface ClassificationRow {
  status: number;
  contentType: string | null;
  kind: VideoDeliveryVerdictKind;
  mode?: 'hls' | 'progressive';
}
interface RetryAfterRow {
  $case: string;
  attempt: number;
  header: string | null;
  delayMs: number;
}
const matrix = JSON.parse(readFileSync(MATRIX, 'utf8')) as {
  classification: ClassificationRow[];
  retryAfter: RetryAfterRow[];
};

/** How the TV screen draws each canonical verdict. Its column of the table. */
const TV_PRESENTATION: Record<VideoDeliveryVerdictKind, TvVideoMode> = {
  ready: 'hls', // refined per row below: hls vs direct comes from the mode
  preparing: 'preparing',
  'not-found': 'error',
  'auth-error': 'error',
  'transient-error': 'error',
  'protocol-error': 'error',
};

test('the shared contract copy is byte-identical to the canonical one', () => {
  // If this fails, run scripts/sync-video-delivery.sh — and check whether the
  // change was meant for all three consumers, because it now applies to none.
  assert.equal(readFileSync(LOCAL_COPY, 'utf8'), readFileSync(CANONICAL, 'utf8'));
});

test('every canonical classification row holds on TV', () => {
  for (const row of matrix.classification) {
    const label = `${row.status} + ${row.contentType ?? 'null'}`;
    const verdict = classifyVideoDelivery(row.status, row.contentType);
    assert.equal(verdict.kind, row.kind, label);
    if (verdict.kind === 'ready') assert.equal(verdict.mode, row.mode, label);

    const expected =
      verdict.kind === 'ready'
        ? (verdict.mode === 'hls' ? 'hls' : 'direct')
        : TV_PRESENTATION[row.kind];
    assert.equal(tvVideoModeFor(verdict), expected, label);
    // The screen's own entry point must agree with the adapter it wraps.
    assert.equal(classifyVideoProbe(row.status, row.contentType), expected, label);
  }
});

test('the MIME is a discriminator, never a playability gate', () => {
  for (const type of ['video/mp4', 'application/octet-stream', null, '', 'text/html']) {
    assert.equal(classifyVideoProbe(206, type), 'direct', String(type));
    assert.equal(classifyVideoProbe(200, type), 'direct', String(type));
  }
  assert.equal(isHlsMime('application/vnd.apple.mpegurl; charset=utf-8'), true);
  assert.equal(isHlsMime('video/mp4'), false);
  assert.equal(isHlsMime(null), false);
});

test('a failed transport classifies like a 5xx, never as a missing file', () => {
  assert.deepEqual(transportFailureVerdict(), { kind: 'transient-error' });
});

test('every canonical Retry-After / backoff row holds on TV', () => {
  for (const row of matrix.retryAfter) {
    assert.equal(nextPreparationDelayMs(row.attempt, row.header), row.delayMs, row.$case);
  }
});

test('a 202 is retried forever, surfacing preparing before every wait', () => {
  let state: VideoDeliveryPollState = INITIAL_POLL_STATE;
  const delays: number[] = [];
  for (let i = 0; i < 40; i += 1) {
    const plan = planNextProbe({ kind: 'preparing', retryAfterMs: null }, state);
    assert.equal(plan.action, 'retry');
    if (plan.action !== 'retry') return;
    assert.equal(plan.surface, 'preparing');
    delays.push(plan.delayMs);
    state = plan.state;
  }
  assert.deepEqual(delays.slice(0, 4), [1500, 2500, 5000, 5000]);
  assert.equal(delays[delays.length - 1], 5000); // a delay cap, not an attempt cap
});

test('a transient boundary is retried a bounded number of times, silently', () => {
  let state: VideoDeliveryPollState = INITIAL_POLL_STATE;
  for (let i = 0; i < TRANSIENT_MAX_RETRIES; i += 1) {
    const plan = planNextProbe({ kind: 'transient-error' }, state);
    assert.equal(plan.action, 'retry');
    if (plan.action !== 'retry') return;
    // Silent: a blip the retry is about to clear must not flash an error pill.
    assert.equal(plan.surface, 'silent');
    state = plan.state;
  }
  assert.deepEqual(planNextProbe({ kind: 'transient-error' }, state), {
    action: 'settle',
    verdict: { kind: 'transient-error' },
  });
});

test('every terminal verdict settles at once', () => {
  const terminal = [
    { kind: 'ready', mode: 'progressive' },
    { kind: 'ready', mode: 'hls' },
    { kind: 'not-found' },
    { kind: 'auth-error' },
    { kind: 'protocol-error' },
  ] as const;
  for (const verdict of terminal) {
    assert.deepEqual(planNextProbe(verdict), { action: 'settle', verdict });
  }
});

// ── The TV screen's wiring ──────────────────────────────────────────────────
// There is no component renderer in this harness, so the player's use of the
// shared policy is pinned against its source. The BEHAVIOUR of that policy is
// what every test above proves.

const player = readSource(import.meta.url, '../components/TvVideoPlayer.tsx');

test('the player runs the SHARED poll policy, with no TV-only interval', () => {
  assert.match(player, /planNextProbe\(verdict, poll\)/);
  assert.match(player, /probeTvVideoDelivery\(videoPath, personal\)/);
  // The 5 s constant this replaces was a TV-only preparing cadence that
  // disagreed with both other clients.
  assert.doesNotMatch(player, /PREPARING_POLL_MS/);
});

test('the player declares contentType for BOTH containers', () => {
  // ExoPlayer cannot infer a container from an extension-less /video URL.
  assert.match(player, /contentType: mode === 'hls' \? 'hls' : 'progressive'/);
});

test('a closed item cannot keep probing: cleanup ends the loop and the wait', () => {
  assert.match(player, /cancelled = true;\s*\n\s*if \(timer\) clearTimeout\(timer\);/);
});

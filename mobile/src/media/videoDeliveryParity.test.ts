// CROSS-CONSUMER PARITY (VIDEO-DELIVERY-PARITY-01) — the mobile column.
//
// frontend, mobile and tv each carry a byte-identical copy of the canonical
// /video contract and each run THIS matrix through it. Two things are asserted
// here, and the same two are asserted by
// frontend/src/video/videoDeliveryParity.test.ts and
// tv/src/video/videoDeliveryParity.test.ts:
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
import { probeStateForOutcome } from '../components/videoPlayback.ts';
import type { VideoProbeState } from '../components/videoPlayback.ts';

const HERE = dirname(fileURLToPath(import.meta.url)); // mobile/src/media
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

/**
 * How the mobile viewer draws each canonical verdict. Its column of the table.
 *
 * auth-error and transient-error are 'error' (a retry button) rather than
 * 'unavailable' because mobile's retry path re-reads the LIVE cookie jar and
 * re-probes, so both are genuinely recoverable by one tap. not-found and
 * protocol-error have nothing to retry.
 */
const MOBILE_PRESENTATION: Record<VideoDeliveryVerdictKind, VideoProbeState> = {
  ready: 'ready',
  preparing: 'preparing',
  'not-found': 'unavailable',
  'protocol-error': 'unavailable',
  'auth-error': 'error',
  'transient-error': 'error',
};

test('the shared contract copy is byte-identical to the canonical one', () => {
  // If this fails, run scripts/sync-video-delivery.sh — and check whether the
  // change was meant for all three consumers, because it now applies to none.
  assert.equal(readFileSync(LOCAL_COPY, 'utf8'), readFileSync(CANONICAL, 'utf8'));
});

test('every canonical classification row holds on mobile', () => {
  for (const row of matrix.classification) {
    const label = `${row.status} + ${row.contentType ?? 'null'}`;
    const verdict = classifyVideoDelivery(row.status, row.contentType);
    assert.equal(verdict.kind, row.kind, label);
    if (verdict.kind === 'ready') assert.equal(verdict.mode, row.mode, label);
    assert.equal(probeStateForOutcome(verdict), MOBILE_PRESENTATION[row.kind], label);
  }
});

test('the MIME is a discriminator, never a playability gate', () => {
  // THE mobile regression this slice closes: a real video whose head arrived
  // without a video/* type used to become permanently "unavailable".
  for (const type of ['video/mp4', 'video/quicktime', 'application/octet-stream', null, '']) {
    assert.deepEqual(
      classifyVideoDelivery(206, type),
      { kind: 'ready', mode: 'progressive' },
      String(type),
    );
    assert.deepEqual(
      classifyVideoDelivery(200, type),
      { kind: 'ready', mode: 'progressive' },
      String(type),
    );
  }
  assert.equal(isHlsMime('application/vnd.apple.mpegurl; charset=utf-8'), true);
  assert.equal(isHlsMime('video/mp4'), false);
  assert.equal(isHlsMime(null), false);
});

test('a failed transport classifies like a 5xx, never as a missing file', () => {
  assert.deepEqual(transportFailureVerdict(), { kind: 'transient-error' });
});

test('cancellation is not a verdict and never reaches the UI', () => {
  assert.equal(probeStateForOutcome({ kind: 'cancelled' }), null);
});

test('every canonical Retry-After / backoff row holds on mobile', () => {
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
  // The mobile-only ceiling of ten preparing attempts is gone: a long but
  // healthy transcode must not become an error because a counter ran out.
  assert.equal(delays[delays.length - 1], 5000);
});

test('a transient boundary is retried a bounded number of times, silently', () => {
  let state: VideoDeliveryPollState = INITIAL_POLL_STATE;
  for (let i = 0; i < TRANSIENT_MAX_RETRIES; i += 1) {
    const plan = planNextProbe({ kind: 'transient-error' }, state);
    assert.equal(plan.action, 'retry');
    if (plan.action !== 'retry') return;
    // Silent: a blip the retry is about to clear must not flash an error.
    assert.equal(plan.surface, 'silent');
    state = plan.state;
  }
  assert.deepEqual(planNextProbe({ kind: 'transient-error' }, state), {
    action: 'settle',
    verdict: { kind: 'transient-error' },
  });
});

test('reaching a 202 clears the transient budget', () => {
  // The connection demonstrably works again, so a later blip gets its own
  // full budget instead of inheriting an exhausted one from before the wait.
  const blip = planNextProbe({ kind: 'transient-error' }, INITIAL_POLL_STATE);
  assert.equal(blip.action, 'retry');
  if (blip.action !== 'retry') return;
  const ready = planNextProbe({ kind: 'preparing', retryAfterMs: null }, blip.state);
  assert.equal(ready.action, 'retry');
  if (ready.action !== 'retry') return;
  assert.equal(ready.state.transientAttempt, 0);
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

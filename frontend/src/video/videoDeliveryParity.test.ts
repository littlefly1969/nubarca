// CROSS-CONSUMER PARITY (VIDEO-DELIVERY-PARITY-01) — the web column.
//
// frontend, mobile and tv each carry a byte-identical copy of the canonical
// /video contract and each run THIS matrix through it. Two things are asserted
// here, and the same two are asserted by mobile/src/media/videoDeliveryParity
// .test.ts and tv/src/video/videoDeliveryParity.test.ts:
//
//   1. the local copy of videoDelivery.ts has not drifted from
//      shared/video-delivery/videoDelivery.ts;
//   2. every row of shared/video-delivery/parity-matrix.json classifies to the
//      same verdict here as it does there, and this consumer's own adapter
//      maps each verdict kind to exactly one presentation.
//
// A rule that changes in one client therefore fails in all three at once, and
// a client can no longer quietly grow its own idea of what /video said.

import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
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
} from './videoDelivery';
import { videoPlaybackModeFor, type VideoPlaybackMode } from './webPlaybackMode';

// Vitest serves modules over http in the jsdom environment, so import.meta.url
// is not a file URL here: the repository is located from the working directory
// instead, which is `frontend/` for every way this suite is run.
function repoRoot(): string {
  let dir = process.cwd();
  for (;;) {
    if (existsSync(resolve(dir, 'shared/video-delivery/videoDelivery.ts'))) return dir;
    const parent = dirname(dir);
    if (parent === dir) throw new Error('shared/video-delivery not found above ' + process.cwd());
    dir = parent;
  }
}
const ROOT = repoRoot();
const CANONICAL = resolve(ROOT, 'shared/video-delivery/videoDelivery.ts');
const MATRIX = resolve(ROOT, 'shared/video-delivery/parity-matrix.json');
const LOCAL_COPY = resolve(ROOT, 'frontend/src/video/videoDelivery.ts');

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

/** How the web viewer draws each canonical verdict. Its column of the table. */
const WEB_PRESENTATION: Record<VideoDeliveryVerdictKind, VideoPlaybackMode> = {
  ready: 'hls', // refined per row below: hls vs direct comes from the mode
  preparing: 'preparing',
  'not-found': 'error',
  'auth-error': 'error',
  'transient-error': 'error',
  'protocol-error': 'error',
};

describe('the shared contract copy', () => {
  it('is byte-identical to shared/video-delivery/videoDelivery.ts', () => {
    // If this fails, run scripts/sync-video-delivery.sh — and check whether the
    // change was meant for all three consumers, because it now applies to none.
    expect(readFileSync(LOCAL_COPY, 'utf8')).toBe(readFileSync(CANONICAL, 'utf8'));
  });
});

describe('canonical classification matrix', () => {
  it.each(matrix.classification)(
    '$status + $contentType -> $kind $mode',
    ({ status, contentType, kind, mode }) => {
      const verdict = classifyVideoDelivery(status, contentType);
      expect(verdict.kind).toBe(kind);
      if (verdict.kind === 'ready') expect(verdict.mode).toBe(mode);

      const expected =
        verdict.kind === 'ready'
          ? (verdict.mode === 'hls' ? 'hls' : 'direct')
          : WEB_PRESENTATION[kind];
      expect(videoPlaybackModeFor(verdict)).toBe(expected);
    },
  );

  it('treats the MIME as a discriminator, never as a playability gate', () => {
    // Everything a 200/206 can carry is playable; only mpegurl means HLS.
    for (const type of ['video/mp4', 'application/octet-stream', null, '', 'text/html']) {
      expect(classifyVideoDelivery(206, type)).toEqual({ kind: 'ready', mode: 'progressive' });
    }
    expect(isHlsMime('application/vnd.apple.mpegurl; charset=utf-8')).toBe(true);
    expect(isHlsMime('video/mp4')).toBe(false);
    expect(isHlsMime(null)).toBe(false);
  });

  it('classifies a failed transport like a 5xx, never as a missing file', () => {
    expect(transportFailureVerdict()).toEqual({ kind: 'transient-error' });
  });
});

describe('canonical Retry-After / backoff matrix', () => {
  it.each(matrix.retryAfter)('$$case', ({ attempt, header, delayMs }) => {
    expect(nextPreparationDelayMs(attempt, header)).toBe(delayMs);
  });
});

describe('canonical poll policy', () => {
  it('retries a 202 forever, surfacing preparing before every wait', () => {
    let state: VideoDeliveryPollState = INITIAL_POLL_STATE;
    const delays: number[] = [];
    for (let i = 0; i < 40; i += 1) {
      const plan = planNextProbe({ kind: 'preparing', retryAfterMs: null }, state);
      expect(plan.action).toBe('retry');
      if (plan.action !== 'retry') return;
      expect(plan.surface).toBe('preparing');
      delays.push(plan.delayMs);
      state = plan.state;
    }
    expect(delays.slice(0, 4)).toEqual([1500, 2500, 5000, 5000]);
    expect(delays.at(-1)).toBe(5000); // no attempt ceiling, only a delay cap
  });

  it('retries a transient boundary a bounded number of times, silently', () => {
    let state: VideoDeliveryPollState = INITIAL_POLL_STATE;
    for (let i = 0; i < TRANSIENT_MAX_RETRIES; i += 1) {
      const plan = planNextProbe({ kind: 'transient-error' }, state);
      expect(plan.action).toBe('retry');
      if (plan.action !== 'retry') return;
      // Silent: a blip the retry is about to clear must not flash an error.
      expect(plan.surface).toBe('silent');
      state = plan.state;
    }
    expect(planNextProbe({ kind: 'transient-error' }, state))
      .toEqual({ action: 'settle', verdict: { kind: 'transient-error' } });
  });

  it('settles every terminal verdict at once', () => {
    for (const verdict of [
      { kind: 'ready', mode: 'progressive' },
      { kind: 'ready', mode: 'hls' },
      { kind: 'not-found' },
      { kind: 'auth-error' },
      { kind: 'protocol-error' },
    ] as const) {
      expect(planNextProbe(verdict)).toEqual({ action: 'settle', verdict });
    }
  });
});

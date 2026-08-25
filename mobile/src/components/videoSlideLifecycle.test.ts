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

test('stale verdicts never reach state: the cancelled guard precedes setProbeState(outcome)', async () => {
  const slide = await sourceOf('VideoSlide.tsx');
  const guard = slide.indexOf('if (cancelled) return;');
  const applyOutcome = slide.indexOf('setProbeState(outcome.phase');
  assert.ok(guard !== -1, 'defence-in-depth cancelled guard missing');
  assert.ok(applyOutcome !== -1, 'outcome application missing');
  assert.ok(
    guard < applyOutcome,
    'outcome may only be applied AFTER the cancelled guard',
  );
});
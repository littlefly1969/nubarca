// Pure sync-policy tests: the retry taxonomy, backoff arithmetic, network
// eligibility, UI-state derivation and operation-key grammar are pinned here
// so the engine can never improvise a different answer.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  backoffDelayMs,
  buildOperationKey,
  classifyHttpFailure,
  deriveUiStatus,
  isUploadAllowed,
  isValidOperationKey,
  mimeFromFilename,
  parseRetryAfterHeader,
} from './syncPolicy.ts';
import type { EngineSnapshot } from './syncTypes.ts';

function snapshot(overrides: Partial<EngineSnapshot>): EngineSnapshot {
  return {
    settings: { enabled: true, wifiOnly: true, includeExisting: false },
    phase: 'working',
    permission: 'granted',
    authRequired: false,
    pendingCount: 0,
    retryableCount: 0,
    permanentCount: 0,
    uploadingCount: 0,
    completedCount: 0,
    skippedCount: 0,
    lastSyncAt: null,
    ...overrides,
  };
}

test('retry taxonomy: transient statuses stay retryable', () => {
  assert.equal(classifyHttpFailure(408).cls, 'retryable-status');
  assert.equal(classifyHttpFailure(429).cls, 'retryable-status');
  assert.equal(classifyHttpFailure(500).cls, 'retryable-status');
  assert.equal(classifyHttpFailure(503).cls, 'retryable-status');
});

test('retry taxonomy: 401 is auth-owned and never spun', () => {
  assert.equal(classifyHttpFailure(401).cls, 'auth');
});

test('retry taxonomy: intentional permanent statuses', () => {
  assert.equal(classifyHttpFailure(403).cls, 'permanent-status');
  assert.equal(classifyHttpFailure(413).cls, 'permanent-status');
  assert.equal(classifyHttpFailure(415).cls, 'permanent-status');
  // With replay-safety on the endpoint, a 409 is a REAL name conflict.
  assert.equal(classifyHttpFailure(409).cls, 'permanent-status');
});

test('retry-after parsing accepts seconds and HTTP dates, caps at policy max', () => {
  const now = 1_000_000;
  assert.equal(parseRetryAfterHeader('120', now, 6 * 3600_000), now + 120_000);
  const date = new Date(now + 30_000).toUTCString();
  assert.equal(parseRetryAfterHeader(date, now, 6 * 3600_000), now + 30_000);
  // A date in the past yields no wait; garbage yields no wait.
  assert.equal(parseRetryAfterHeader(new Date(now - 60_000).toUTCString(), now, 6 * 3600_000), null);
  assert.equal(parseRetryAfterHeader('soon', now, 6 * 3600_000), null);
  // Cap: a huge Retry-After cannot outlive policy.
  assert.equal(
    parseRetryAfterHeader('999999', now, 1000),
    now + 1000,
  );
});

test('backoff grows exponentially but full-jitter keeps it inside [0, window)', () => {
  const config = { baseMs: 30_000, maxMs: 6 * 3600_000 };
  let zero = 0;
  for (let i = 0; i < 50; i++) {
    if (backoffDelayMs(1, config, () => 0) === 0) zero += 1;
    assert.ok(backoffDelayMs(3, config, () => 0.999) <= 120_000);
    assert.ok(backoffDelayMs(20, config, () => 0.999) <= config.maxMs);
    assert.ok(backoffDelayMs(2, config, () => 0) >= 0);
  }
  assert.ok(zero > 0, 'zero jitter must be allowed');
});

test('network eligibility follows the Wi-Fi-only switch', () => {
  assert.equal(isUploadAllowed(true, 'wifi'), true);
  assert.equal(isUploadAllowed(true, 'cellular'), false);
  assert.equal(isUploadAllowed(false, 'cellular'), true);
  assert.equal(isUploadAllowed(false, 'none'), false);
  assert.equal(isUploadAllowed(false, 'unknown'), false);
});

test('UI status derivation covers every user-visible state', () => {
  const base = {
    pendingCount: 0,
    retryableCount: 0,
    permanentCount: 0,
    uploadingCount: 0,
  };
  assert.equal(
    deriveUiStatus(snapshot({ ...base, settings: { enabled: false, wifiOnly: true, includeExisting: false }, phase: 'idle' })),
    'off',
  );
  assert.equal(deriveUiStatus(snapshot({ ...base, permission: 'undetermined' })), 'permission-required');
  assert.equal(deriveUiStatus(snapshot({ ...base, permission: 'denied' })), 'permission-required');
  assert.equal(deriveUiStatus(snapshot({ ...base, phase: 'paused' })), 'paused');
  assert.equal(deriveUiStatus(snapshot({ ...base, phase: 'paused', authRequired: true })), 'auth-required');
  assert.equal(
    deriveUiStatus(snapshot({ ...base, pendingCount: 3, retryableCount: 1, phase: 'waiting-network' })),
    'waiting-wifi',
  );
  assert.equal(
    deriveUiStatus(snapshot({ ...base, uploadingCount: 2, pendingCount: 5, phase: 'working' })),
    'uploading',
  );
  assert.equal(deriveUiStatus(snapshot({ ...base, phase: 'discovering' })), 'scanning');
  assert.equal(deriveUiStatus(snapshot({ ...base, permanentCount: 1, phase: 'idle' })), 'attention');
  assert.equal(
    deriveUiStatus(snapshot({ ...base, retryableCount: 4, phase: 'waiting-network', settings: { enabled: true, wifiOnly: false, includeExisting: false } })),
    'pending',
  );
  assert.equal(deriveUiStatus(snapshot({ ...base, phase: 'idle' })), 'up-to-date');
});

test('operation keys are stable per logical op, distinct across ops and grammar-safe', () => {
  const key = buildOperationKey('account-1', 'asset-42', 1700000000000);
  assert.equal(key, buildOperationKey('account-1', 'asset-42', 1700000000000));
  // Same asset, different revision → different LOGICAL operation.
  assert.notEqual(key, buildOperationKey('account-1', 'asset-42', 1700000001000));
  // Different owner → different key even for identical asset coordinates.
  assert.notEqual(key, buildOperationKey('account-2', 'asset-42', 1700000000000));
  assert.ok(isValidOperationKey(key));
  assert.ok(!isValidOperationKey('short'));
  assert.ok(!isValidOperationKey('has spaces inside'));
});

test('MIME hints cover common camera media without pretending authority', () => {
  assert.equal(mimeFromFilename('IMG_20240101_101010.jpg'), 'image/jpeg');
  assert.equal(mimeFromFilename('VID_20240101_101010.mp4'), 'video/mp4');
  assert.equal(mimeFromFilename('IMG_0001.HEIC'), 'image/heic');
  assert.equal(mimeFromFilename('PXL_202401.mov'), 'video/quicktime');
  assert.equal(mimeFromFilename('no-extension'), 'application/octet-stream');
  assert.equal(mimeFromFilename(null), 'application/octet-stream');
});


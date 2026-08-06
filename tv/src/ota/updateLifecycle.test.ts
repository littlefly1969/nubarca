import assert from 'node:assert/strict';
import test from 'node:test';
import { resetUpdateLifecycleForTests, startBackgroundUpdateCheck } from './updateLifecycle.ts';

function api(overrides: Record<string, unknown> = {}) {
  return {
    isEnabled: true, runtimeVersion: 'tv-native-1', updateId: 'embedded', isEmbeddedLaunch: true,
    checkForUpdateAsync: async () => ({ isAvailable: false }),
    fetchUpdateAsync: async () => ({ isNew: false }),
    ...overrides,
  } as never;
}

test.beforeEach(resetUpdateLifecycleForTests);

test('starts in the background and prevents overlapping or repeated checks', async () => {
  let checks = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const fake = api({ checkForUpdateAsync: async () => { checks += 1; await gate; return { isAvailable: false }; } });
  const first = startBackgroundUpdateCheck(fake);
  const second = startBackgroundUpdateCheck(fake);
  assert.equal(first, second);
  assert.equal(checks, 1);
  release();
  assert.equal((await first).lastResult, 'no-update');
  await startBackgroundUpdateCheck(fake);
  assert.equal(checks, 1);
});

test('downloads without reloading and records a pending update', async () => {
  let fetches = 0;
  const result = await startBackgroundUpdateCheck(api({
    checkForUpdateAsync: async () => ({ isAvailable: true }),
    fetchUpdateAsync: async () => { fetches += 1; return { isNew: true }; },
  }));
  assert.equal(fetches, 1);
  assert.equal(result.pending, true);
  assert.equal(result.lastResult, 'downloaded');
});

test('boot diagnostics expose only non-secret release and update identity', async () => {
  const lines: unknown[][] = [];
  const original = console.info;
  console.info = (...args: unknown[]) => { lines.push(args); };
  try {
    const result = await startBackgroundUpdateCheck(api(), {
      applicationVersion: '1.0.1', versionCode: 2, channel: 'production',
    });
    assert.equal(result.applicationVersion, '1.0.1');
    assert.equal(result.versionCode, 2);
    assert.equal(result.channel, 'production');
    assert.deepEqual(lines[0], ['[TV_BOOT]', {
      applicationVersion: '1.0.1', versionCode: 2, runtimeVersion: 'tv-native-1',
      channel: 'production', updateId: 'embedded', embeddedLaunch: true,
    }]);
  } finally {
    console.info = original;
  }
});

test('swallows and records update errors', async () => {
  const result = await startBackgroundUpdateCheck(api({
    checkForUpdateAsync: async () => { throw new Error('offline'); },
  }));
  assert.equal(result.lastResult, 'error');
  assert.equal(result.lastError, 'offline');
});

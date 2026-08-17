import assert from 'node:assert/strict';
import test from 'node:test';
import {
  applyPendingUpdate,
  checkForUpdateNow,
  resetUpdateLifecycleForTests,
  startBackgroundUpdateCheck,
} from './updateLifecycle.ts';

// Every fake counts its own calls: the rules being protected here are mostly
// "exactly once" and "never", which a return value alone cannot express.
function api(overrides: Record<string, unknown> = {}) {
  return {
    isEnabled: true, runtimeVersion: 'tv-native-1', updateId: 'embedded', isEmbeddedLaunch: true,
    checkForUpdateAsync: async () => ({ isAvailable: false }),
    fetchUpdateAsync: async () => ({ isNew: false }),
    reloadAsync: async () => {},
    ...overrides,
  } as never;
}

interface CountingOptions {
  available?: boolean;
  isEnabled?: boolean;
  reloadAsync?: () => Promise<void>;
}

function counting(options: CountingOptions = {}) {
  const calls = { checks: 0, fetches: 0, reloads: 0 };
  const fake = api({
    isEnabled: options.isEnabled ?? true,
    checkForUpdateAsync: async () => {
      calls.checks += 1;
      return { isAvailable: options.available ?? false };
    },
    fetchUpdateAsync: async () => { calls.fetches += 1; return { isNew: true }; },
    reloadAsync: options.reloadAsync ?? (async () => { calls.reloads += 1; }),
  });
  return { fake, calls };
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

test('the startup prefetch never reloads the app', async () => {
  // A background reload would interrupt a running slideshow or Beauty Lab
  // analysis. A downloaded update stays pending until the user asks, or until
  // the next cold launch.
  const { fake, calls } = counting({ available: true });
  await startBackgroundUpdateCheck(fake);
  assert.equal(calls.reloads, 0);
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

// --- the explicit user check -------------------------------------------------

test('an explicit check runs again after the startup check found nothing', async () => {
  // The whole point of the button: a no-update answer from boot is not an
  // answer to a question the user asks minutes later.
  const { fake, calls } = counting();
  await startBackgroundUpdateCheck(fake);
  assert.equal(calls.checks, 1);
  assert.deepEqual(await checkForUpdateNow(fake), { state: 'up-to-date' });
  assert.equal(calls.checks, 2);
  assert.equal(calls.reloads, 0);
});

test('an explicit check joins a startup check that is still running', async () => {
  let checks = 0;
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const fake = api({
    checkForUpdateAsync: async () => { checks += 1; await gate; return { isAvailable: false }; },
  });
  const startup = startBackgroundUpdateCheck(fake);
  const manual = checkForUpdateNow(fake);
  release();
  assert.deepEqual(await manual, { state: 'up-to-date' });
  await startup;
  // expo-updates does not tolerate overlapping check/fetch calls; arriving on
  // the update screen mid-boot must join, never duplicate.
  assert.equal(checks, 1);
});

test('an available update is fetched exactly once and offered as ready', async () => {
  const { fake, calls } = counting({ available: true });
  assert.deepEqual(await checkForUpdateNow(fake), { state: 'ota-ready' });
  assert.equal(calls.checks, 1);
  assert.equal(calls.fetches, 1);
});

test('an already-downloaded update is not checked or fetched a second time', async () => {
  const { fake, calls } = counting({ available: true });
  await startBackgroundUpdateCheck(fake);
  assert.equal(calls.fetches, 1);
  assert.deepEqual(await checkForUpdateNow(fake), { state: 'ota-ready' });
  // expo-updates already holds the bytes; re-fetching buys nothing.
  assert.equal(calls.checks, 1);
  assert.equal(calls.fetches, 1);
});

test('a disabled updates runtime answers safely and touches nothing', async () => {
  const { fake, calls } = counting({ isEnabled: false });
  assert.deepEqual(await checkForUpdateNow(fake), { state: 'disabled' });
  assert.equal(calls.checks, 0);
  assert.equal(calls.reloads, 0);
});

test('a failed explicit check is retryable and never reloads', async () => {
  const calls = { checks: 0, reloads: 0 };
  let fail = true;
  const fake = api({
    checkForUpdateAsync: async () => {
      calls.checks += 1;
      if (fail) throw new Error('offline');
      return { isAvailable: false };
    },
    reloadAsync: async () => { calls.reloads += 1; },
  });
  assert.deepEqual(await checkForUpdateNow(fake), { state: 'error', message: 'offline' });
  assert.equal(calls.reloads, 0);
  fail = false;
  assert.deepEqual(await checkForUpdateNow(fake), { state: 'up-to-date' });
  assert.equal(calls.checks, 2);
});

// --- install now -------------------------------------------------------------

test('install now reloads exactly once', async () => {
  const { fake, calls } = counting();
  assert.deepEqual(await applyPendingUpdate(fake), { state: 'reloading' });
  assert.equal(calls.reloads, 1);
});

test('pressing install again while applying does not reload twice', async () => {
  // The remote repeats easily and the screen stays up until the runtime
  // actually restarts, so this is the ordinary case, not an edge one.
  let release!: () => void;
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const { fake, calls } = counting({ reloadAsync: async () => { await gate; calls.reloads += 1; } });
  const first = applyPendingUpdate(fake);
  const second = applyPendingUpdate(fake);
  assert.equal(first, second);
  release();
  await first;
  assert.equal(calls.reloads, 1);
  // And once accepted it stays terminal: a later press is still not a reload.
  await applyPendingUpdate(fake);
  assert.equal(calls.reloads, 1);
});

test('a refused reload is a safe, retryable error', async () => {
  const calls = { reloads: 0 };
  let refuse = true;
  const fake = api({
    reloadAsync: async () => {
      calls.reloads += 1;
      if (refuse) throw new Error('reload refused');
    },
  });
  assert.deepEqual(await applyPendingUpdate(fake), { state: 'error', message: 'reload refused' });
  refuse = false;
  assert.deepEqual(await applyPendingUpdate(fake), { state: 'reloading' });
  assert.equal(calls.reloads, 2);
});

test('nothing meaningful is recorded after a reload is accepted', async () => {
  // reloadAsync() tearing down the JS runtime means no continuation is
  // guaranteed to run. Anything this path wrote afterwards would be state the
  // product silently depends on and may never get.
  const { fake } = counting({ available: true });
  const before = await startBackgroundUpdateCheck(fake);
  await applyPendingUpdate(fake);
  const { getOtaDiagnostics } = await import('./updateLifecycle.ts');
  assert.deepEqual(getOtaDiagnostics(), before);
});

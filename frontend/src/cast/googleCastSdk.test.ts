import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { CAST_SENDER_SCRIPT } from './castSdkTypes';
import {
  browserSupportsCastSender,
  isReceiverReachableOrigin,
  loadGoogleCastSdk,
  resetGoogleCastSdkForTests,
} from './googleCastSdk';
import { createFakeCastSdk } from './castTestDouble';

// The loader has exactly two properties worth defending, and both fail SILENTLY
// when broken — a Cast button that is simply never enabled, or duplicated
// session events nobody traces back to a second script tag.
describe('googleCastSdk loader', () => {
  beforeEach(() => {
    resetGoogleCastSdkForTests();
    document.head.querySelectorAll('script').forEach((s) => { s.remove(); });
    delete (window as { chrome?: unknown }).chrome;
    delete (window as { cast?: unknown }).cast;
  });

  afterEach(() => {
    resetGoogleCastSdkForTests();
    delete (window as { chrome?: unknown }).chrome;
    delete (window as { cast?: unknown }).cast;
  });

  it('reports unsupported when the browser has no Cast bridge', async () => {
    expect(browserSupportsCastSender()).toBe(false);

    const result = await loadGoogleCastSdk();

    expect(result.status).toBe('unsupported');
    // Nothing is fetched from Google for a browser that could never use it.
    expect(document.querySelector(`script[src="${CAST_SENDER_SCRIPT}"]`)).toBeNull();
  });

  it('installs the readiness callback BEFORE appending the script', async () => {
    // The bridge exists (Chromium) but the framework is not loaded yet.
    (window as { chrome?: unknown }).chrome = {};

    const load = loadGoogleCastSdk();

    // The SDK reads this synchronously when it finishes loading; installed
    // afterwards it would never be called and Cast would silently never work.
    expect(typeof window.__onGCastApiAvailable).toBe('function');
    const script = document.querySelector<HTMLScriptElement>(
      `script[src="${CAST_SENDER_SCRIPT}"]`,
    );
    expect(script).not.toBeNull();
    expect(script!.async).toBe(true);

    // Now let the "SDK" arrive.
    const fake = createFakeCastSdk();
    (window as { cast?: unknown }).cast = { framework: fake.framework };
    (window as { chrome?: unknown }).chrome = fake.chrome;
    window.__onGCastApiAvailable!(true);

    const result = await load;
    expect(result.status).toBe('ready');
  });

  it('appends the script at most once no matter how often it is asked', async () => {
    (window as { chrome?: unknown }).chrome = {};

    const first = loadGoogleCastSdk();
    const second = loadGoogleCastSdk();
    const third = loadGoogleCastSdk();

    expect(document.querySelectorAll(`script[src="${CAST_SENDER_SCRIPT}"]`)).toHaveLength(1);
    expect(second).toBe(first);
    expect(third).toBe(first);

    const fake = createFakeCastSdk();
    (window as { cast?: unknown }).cast = { framework: fake.framework };
    (window as { chrome?: unknown }).chrome = fake.chrome;
    window.__onGCastApiAvailable!(true);
    await expect(first).resolves.toMatchObject({ status: 'ready' });
  });

  it('fails cleanly when the SDK reports itself unavailable', async () => {
    (window as { chrome?: unknown }).chrome = {};
    const load = loadGoogleCastSdk();

    window.__onGCastApiAvailable!(false, 'blocked');

    await expect(load).resolves.toEqual({ status: 'failed' });
  });

  it('resolves immediately when the framework is already present', async () => {
    const fake = createFakeCastSdk();
    (window as { cast?: unknown }).cast = { framework: fake.framework };
    (window as { chrome?: unknown }).chrome = fake.chrome;

    const result = await loadGoogleCastSdk();

    expect(result.status).toBe('ready');
    expect(document.querySelector(`script[src="${CAST_SENDER_SCRIPT}"]`)).toBeNull();
  });

  // A television cannot resolve a loopback address, however secure the browser
  // considers it. That distinction is the difference between a useful message
  // and "Cast is unavailable".
  it('treats loopback hosts as unreachable by a receiver', () => {
    expect(isReceiverReachableOrigin()).toBe(false);
  });
});

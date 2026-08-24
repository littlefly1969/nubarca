// LOCAL-FIRST logout tests (acceptance BLOCKER 8): the local teardown must
// run BEFORE any network word, the captured pre-teardown cookie is used
// exclusively for that one best-effort notification, and a never-resolving
// server call can neither resurrect the session nor block anything.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { signOutLocalFirst } from './signOut.ts';
import { sessionCookieSource, setSessionCookieSource } from './sessionAccess.ts';

interface RecordedRequest {
  url: string;
  cookie: string | null;
}

test('local teardown runs first; the captured cookie rides the best-effort call', async () => {
  let tornDown = false;
  const seenCookies: Array<string | null> = [];
  const originalFetch = globalThis.fetch;
  // The server NEVER answers here — and that must not matter at all.
  globalThis.fetch = ((_url: string | URL, init?: { headers?: Record<string, string> }) => {
    seenCookies.push(init?.headers?.cookie ?? null);
    return new Promise(() => undefined);
  }) as typeof fetch;

  setSessionCookieSource({
    current: 'NubArca.Auth=OLD-SESSION',
    capture: () => {},
  });

  try {
    const handle = signOutLocalFirst(() => {
      // EXACTLY what the real provider's callback does: wipe the local state.
      tornDown = true;
      setSessionCookieSource({ current: null, capture: () => {} });
    });

    await new Promise((r) => setImmediate(r));
    // Teardown happened BEFORE the network settled…
    assert.equal(tornDown, true);
    // …the seam is dead while the notification is still flying…
    assert.equal(sessionCookieSource().current, null);
    // …and the notification carries the CAPTURED pre-teardown cookie.
    assert.deepEqual(seenCookies, ['NubArca.Auth=OLD-SESSION']);

    // Restore a resolving fetch so the tracked notification can settle and
    // this test can end cleanly.
    globalThis.fetch = (() =>
      Promise.resolve({ ok: true, status: 200 } as Response)) as typeof fetch;
    await Promise.race([handle.serverNotification, new Promise((r) => setImmediate(r))]);
    assert.equal(tornDown, true);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('the notification carries the PRE-teardown cookie via override', async () => {
  const recorded: RecordedRequest[] = [];
  setSessionCookieSource({
    current: 'NubArca.Auth=LIVE-COOKIE',
    capture: () => {},
  });
  const originalFetch = globalThis.fetch;
  globalThis.fetch = ((url: string | URL, init?: { headers?: Record<string, string> }) => {
    recorded.push({
      url: String(url),
      cookie: init?.headers?.cookie ?? null,
    });
    return Promise.resolve({ ok: true, status: 200 } as Response);
  }) as typeof fetch;

  try {
    await signOutLocalFirst(() => {
      /* local teardown happened */
    });
  } finally {
    globalThis.fetch = originalFetch;
  }

  assert.equal(recorded.length, 1);
  assert.ok(recorded[0].url.endsWith('/api/auth/logout'), recorded[0].url);
  // The OLD cookie — captured before the wipe — is what reached the server.
  assert.equal(recorded[0].cookie, 'NubArca.Auth=LIVE-COOKIE');
});

test('signed-out logout still tears down locally and calls NOTHING', async () => {
  const recorded: RecordedRequest[] = [];
  setSessionCookieSource({ current: null, capture: () => {} });
  const originalFetch = globalThis.fetch;
  globalThis.fetch = ((url: string | URL) => {
    recorded.push({ url: String(url), cookie: null });
    return Promise.resolve({ ok: true, status: 200 } as Response);
  }) as typeof fetch;

  try {
    let tornDown = false;
    await signOutLocalFirst(() => {
      tornDown = true;
    });
    assert.equal(tornDown, true);
    assert.equal(recorded.length, 0, 'no server call without a captured cookie');
  } finally {
    globalThis.fetch = originalFetch;
  }
});

test('a FAILING server notification cannot fail the logout', async () => {
  setSessionCookieSource({
    current: 'NubArca.Auth=X',
    capture: () => {},
  });
  const originalFetch = globalThis.fetch;
  globalThis.fetch = (() => Promise.reject(new TypeError('Network request failed'))) as typeof fetch;

  try {
    let tornDown = false;
    const { serverNotification } = signOutLocalFirst(() => {
      tornDown = true;
    });
    // The notification swallows its own failure; awaiting it stays safe.
    await assert.doesNotReject(serverNotification);
    assert.equal(tornDown, true);
  } finally {
    globalThis.fetch = originalFetch;
  }
});

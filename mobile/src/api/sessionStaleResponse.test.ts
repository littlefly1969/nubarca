// Stale-response regression tests (MOBILE-SESSION-LIFECYCLE-01).
//
// THE INVARIANT: a response belonging to an OLD authenticated session must
// never mutate the live session cookie — not after a logout, and not across
// an account switch. Ownership is decided by the SESSION GENERATION captured
// when the request started, never by comparing cookie values.
//
// These tests wire the REAL OwnerSessionCookieStore behind the REAL seam the
// way SessionProvider does, and drive requests through the REAL client, so
// the race is exercised end-to-end instead of against a mock.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { apiGet, apiPost, configureBaseUrl } from './client.ts';
import {
  OwnerSessionCookieStore,
  SESSION_STORAGE_KEY,
  type SessionCookieStorage,
} from './sessionCookie.ts';
import { setSessionCookieSource } from './sessionAccess.ts';

const A = 'NubArca.Auth=account-A-session';
const A_RENEWED = 'NubArca.Auth=renewed-A';
const B = 'NubArca.Auth=account-B-session';

function fakeStorage() {
  const map = new Map<string, string>();
  const storage: SessionCookieStorage = {
    getItem: async (key) => map.get(key) ?? null,
    setItem: async (key, value) => {
      map.set(key, value);
    },
    removeItem: async (key) => {
      map.delete(key);
    },
  };
  return { storage, persisted: () => map.get(SESSION_STORAGE_KEY) ?? null };
}

// Production-shaped wiring: the seam DELEGATES to the store exactly the way
// SessionProvider does.
function wireProductionShapedSeam(store: OwnerSessionCookieStore): void {
  setSessionCookieSource({
    get current() {
      return store.current;
    },
    snapshot() {
      return store.snapshot();
    },
    captureIfCurrent(setCookie, generation) {
      return store.captureIfCurrent(setCookie, generation);
    },
  });
}

// Caller-controlled server: every started request parks until the test hands
// back a status + Set-Cookie, so a response can land AFTER the session has
// already moved on — the exact shape of the stale-response race. Requests
// are answered by START ORDER index, but resolution order is free.
const responders: Array<(status: number, setCookie: string | null) => void> = [];
let originalFetch: typeof fetch | null = null;

function installControlledFetch(): void {
  originalFetch = globalThis.fetch;
  responders.length = 0;
  globalThis.fetch = ((_url: string | URL) =>
    new Promise<Response>((resolve) => {
      responders.push((status, setCookie) => {
        resolve(
          new Response('{}', {
            status,
            headers: setCookie === null ? {} : { 'set-cookie': setCookie },
          }),
        );
      });
    })) as typeof fetch;
}

test.afterEach(() => {
  if (originalFetch !== null) {
    globalThis.fetch = originalFetch;
    originalFetch = null;
  }
});

async function settle(): Promise<void> {
  await new Promise((done) => setImmediate(done));
}

test('a response completing after LOGOUT cannot resurrect the session', async () => {
  configureBaseUrl('https://unit.test');
  const { storage, persisted } = fakeStorage();
  const store = new OwnerSessionCookieStore(storage);
  wireProductionShapedSeam(store);

  // GIVEN session A is authenticated…
  await store.capture(A);
  assert.equal(store.current, A);

  // …AND authenticated request R starts under session generation A.
  installControlledFetch();
  const requestR = apiGet('/api/auth/me');

  // WHEN logout clears the local session — the generation moves…
  await store.clear();
  assert.equal(store.current, null);

  // …AND request R later completes with Set-Cookie renewed-A.
  respond(0, 200, A_RENEWED);
  await requestR;
  await settle();

  // THEN the current session remains unauthenticated: renewed-A is neither
  // restored in memory nor persisted.
  assert.equal(store.current, null);
  assert.equal(persisted(), null);
});

test('a stale account-A response cannot overwrite account B', async () => {
  configureBaseUrl('https://unit.test');
  const { storage, persisted } = fakeStorage();
  const store = new OwnerSessionCookieStore(storage);
  wireProductionShapedSeam(store);

  // Request belonging to account A starts.
  await store.capture(A);
  installControlledFetch();
  const staleRequest = apiGet('/api/albums');

  // Logout A …
  await store.clear();

  // … login account B through the REAL request path: its fresh Set-Cookie
  // MUST still be accepted (normal login capture keeps working). Start the
  // request FIRST so its responder registers, then answer it.
  const loginB = apiPost('/api/auth/login', { email: 'b@unit.test', password: 'pw' }, {
    allow401: true,
  });
  respond(1, 200, B); // responder order: [0] = staleRequest, [1] = login B
  await loginB;
  await settle();
  assert.equal(store.current, B);

  // The stale response from A arrives AFTER B took over.
  respond(0, 200, A_RENEWED);
  await staleRequest;
  await settle();

  // Current session == B; A2 is discarded from memory AND persistence.
  assert.equal(store.current, B);
  assert.equal(persisted(), B);
});

test('sliding-session renewal is still accepted while no logout happened', async () => {
  configureBaseUrl('https://unit.test');
  const { storage, persisted } = fakeStorage();
  const store = new OwnerSessionCookieStore(storage);
  wireProductionShapedSeam(store);

  await store.capture(A);
  installControlledFetch();
  const renewal = apiGet('/api/auth/me');
  respond(0, 200, A_RENEWED);
  await renewal;
  await settle();

  // Same live session, same generation: the rotated cookie is captured and
  // persisted exactly as before the guard existed.
  assert.equal(store.current, A_RENEWED);
  assert.equal(persisted(), A_RENEWED);
});

function respond(requestIndex: number, status: number, setCookie: string | null): void {
  responders[requestIndex](status, setCookie);
}
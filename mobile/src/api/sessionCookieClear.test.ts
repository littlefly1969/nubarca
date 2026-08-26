// Durable-clear tracking tests (acceptance BLOCKER 3): clear() must wipe
// memory SYNCHRONOUSLY while handing back a promise that completes only when
// the SecureStore removal has actually landed.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { OwnerSessionCookieStore, SESSION_STORAGE_KEY } from './sessionCookie.ts';

function fakeStorage() {
  const map = new Map<string, string>();
  const removed: string[] = [];
  return {
    storage: {
      getItem: async (k: string) => map.get(k) ?? null,
      setItem: async (k: string, v: string) => {
        map.set(k, v);
      },
      removeItem: async (k: string) => {
        removed.push(k);
        map.delete(k);
      },
    },
    removed,
    has: (k: string) => map.has(k),
  };
}

test('clear wipes memory synchronously and tracks the durable removal', async () => {
  const { storage, removed } = fakeStorage();
  const store = new OwnerSessionCookieStore(storage);
  await store.capture('NubArca.Auth=live-cookie');
  assert.equal(store.current, 'NubArca.Auth=live-cookie');

  const durable = store.clear();
  // BEFORE awaiting anything: the in-memory jar is already dead.
  assert.equal(store.current, null);

  await durable; // the caller CAN track completion without blocking the UI
  assert.deepEqual(removed, [SESSION_STORAGE_KEY]);
});

test('clear never rejects even if SecureStore removal fails', async () => {
  const store = new OwnerSessionCookieStore({
    getItem: async () => null,
    setItem: async () => undefined,
    removeItem: async () => {
      throw new Error('disk gone');
    },
  });
  store.capture('NubArca.Auth=x');
  const durable = store.clear();
  await assert.doesNotReject(durable);
  assert.equal(store.current, null);
});


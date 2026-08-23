// Session-cookie tests: the teeth of the mobile session layer. The comma
// cases here are the regression suite for the retired split(',') parser —
// restoring that parser must fail this file (verified by the slice's negative
// checks). Run with `node --test src/api/sessionCookie.test.ts`.

import assert from 'node:assert/strict';
import test from 'node:test';
import {
  normalizeOwnerSessionCookie,
  OwnerSessionCookieStore,
  type SessionCookieStorage,
} from './sessionCookie.ts';

const exact = `NubArca.Auth=${'s'.repeat(40)}`;

function memoryStorage(initial: string | null = null) {
  let value = initial;
  const operations: string[] = [];
  const storage: SessionCookieStorage = {
    getItem: async () => value,
    setItem: async (_key, next) => {
      operations.push('set');
      value = next;
    },
    removeItem: async () => {
      operations.push('remove');
      value = null;
    },
  };
  return {
    storage,
    operations,
    get value() {
      return value;
    },
  };
}

function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

test('extracts the exact owner cookie from one Set-Cookie with Expires', () => {
  // THE comma case: Expires carries a comma; blind splitting corrupts.
  assert.equal(
    normalizeOwnerSessionCookie(
      `${exact}; expires=Wed, 21 Oct 2026 07:28:00 GMT; path=/; secure; httponly; samesite=lax`,
    ),
    exact,
  );
});

test('extracts the owner cookie after an unrelated comma-separated cookie', () => {
  assert.equal(
    normalizeOwnerSessionCookie(
      `Other=value; expires=Tue, 15 Sep 2026 12:00:00 GMT, ${exact}; path=/`,
    ),
    exact,
  );
});

test('extracts the owner cookie before unrelated cookies and attributes', () => {
  assert.equal(
    normalizeOwnerSessionCookie(
      `${exact}; path=/; HttpOnly, NubArca.Csrf=x; expires=Fri, 01 Jan 2027 00:00:00 GMT`,
    ),
    exact,
  );
});

test('multiple Set-Cookie values keep only the owner pair', () => {
  assert.equal(
    normalizeOwnerSessionCookie(
      `a=1; expires=Wed, 21 Oct 2026 07:28:00 GMT, ${exact}; httponly, b=2`,
    ),
    exact,
  );
});

test('unrelated cookies alone extract nothing', () => {
  assert.equal(normalizeOwnerSessionCookie('NubArca.TvSession=tv-secret; path=/api/tv'), null);
  assert.equal(normalizeOwnerSessionCookie('NubArca.AuthX=nope'), null);
  assert.equal(normalizeOwnerSessionCookie('session=abc'), null);
});

test('malformed input fails safely to null', () => {
  assert.equal(normalizeOwnerSessionCookie(null), null);
  assert.equal(normalizeOwnerSessionCookie(''), null);
  assert.equal(normalizeOwnerSessionCookie('NubArca.Auth='), null);
  assert.equal(normalizeOwnerSessionCookie('NubArca.Auth'), null);
  assert.equal(normalizeOwnerSessionCookie(';;;,,,==='), null);
});

test('cold restore normalizes a legacy attribute-fragment value', async () => {
  // What the OLD comma-splitting parser would have persisted.
  const memory = memoryStorage(
    'NubArca.Auth=legacy; Path=/; Expires=Wed; 21 Oct 2026 07:28:00 GMT',
  );
  const store = new OwnerSessionCookieStore(memory.storage);
  assert.equal(await store.restore(), true);
  assert.equal(store.current, 'NubArca.Auth=legacy');
});

test('restore returns false when nothing is stored', async () => {
  const store = new OwnerSessionCookieStore(memoryStorage().storage);
  assert.equal(await store.restore(), false);
  assert.equal(store.current, null);
});

test('capture ignores responses without the owner cookie', async () => {
  const memory = memoryStorage();
  const store = new OwnerSessionCookieStore(memory.storage);
  await store.capture('Other=x; expires=Wed, 21 Oct 2026 07:28:00 GMT');
  assert.equal(store.current, null);
  await new Promise((done) => setTimeout(done, 0));
  assert.equal(memory.value, null);
});

test('capture persists exactly the extracted pair', async () => {
  const memory = memoryStorage();
  const store = new OwnerSessionCookieStore(memory.storage);
  await store.capture(`${exact}; expires=Wed, 21 Oct 2026 07:28:00 GMT; httponly`);
  assert.equal(store.current, exact);
  await store.ensure();
  assert.equal(memory.value, exact);
});

test('clear wins over an in-flight restore', async () => {
  const memory = memoryStorage(exact);
  const gate = deferred<void>();
  const storage: SessionCookieStorage = {
    getItem: async () => {
      await gate.promise;
      return memory.value;
    },
    setItem: async () => {
      /* not exercised by this test */
    },
    removeItem: async () => {
      /* best-effort clear */
    },
  };
  const store = new OwnerSessionCookieStore(storage);
  const restoring = store.restore();
  store.clear();
  gate.resolve();
  assert.equal(await restoring, false);
  assert.equal(store.current, null);
});

test('clear wins over a slow capture persist', async () => {
  const writeStarted = deferred<void>();
  const releaseWrite = deferred<void>();
  const storage: SessionCookieStorage = {
    getItem: async () => null,
    setItem: async (_key) => {
      writeStarted.resolve();
      await releaseWrite.promise;
      wroteToDisk = true;
    },
    removeItem: async () => {
      removedFromDisk = true;
    },
  };
  let wroteToDisk = false;
  let removedFromDisk = false;
  const store = new OwnerSessionCookieStore(storage);
  const capturing = store.capture(exact);
  await writeStarted.promise;
  // Logout happens WHILE the login-time persist is still in flight.
  store.clear();
  releaseWrite.resolve();
  await capturing;
  await new Promise((done) => setTimeout(done, 0));
  assert.equal(store.current, null);
  assert.equal(wroteToDisk, true); // the write did physically happen…
  assert.equal(removedFromDisk, true); // …and clear still ran after it.
});

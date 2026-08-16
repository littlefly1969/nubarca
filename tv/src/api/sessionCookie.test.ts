import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';
import {
  normalizeTvSessionCookie,
  TvSessionCookieStore,
  type SessionCookieStorage,
} from './sessionCookie.ts';

const exact = `NubArca.TvSession=${'a'.repeat(43)}`;
const apiDir = dirname(fileURLToPath(import.meta.url));

function deferred() {
  let resolve!: () => void;
  return { promise: new Promise<void>((done) => { resolve = done; }), resolve };
}

function memoryStorage(initial: string | null = null) {
  let value = initial;
  const operations: string[] = [];
  const storage: SessionCookieStorage = {
    getItem: async () => value,
    setItem: async (_key, next) => { operations.push('set'); value = next; },
    removeItem: async () => { operations.push('remove'); value = null; },
  };
  return {
    storage,
    operations,
    get value() { return value; },
    set value(next: string | null) { value = next; },
  };
}

test('extracts only the exact TV session cookie from Set-Cookie headers', () => {
  assert.equal(normalizeTvSessionCookie(
    `${exact}; expires=Tue, 15 Sep 2026 12:00:00 GMT; path=/api/tv; secure; httponly`,
  ), exact);
  assert.equal(normalizeTvSessionCookie(
    `Other=value; expires=Tue, 15 Sep 2026 12:00:00 GMT, ${exact}; path=/api/tv`,
  ), exact);
  assert.equal(normalizeTvSessionCookie('NubArca.Auth=owner-secret; path=/'), null);
});

test('cold restore normalizes the legacy Expires fragment', async () => {
  const memory = memoryStorage(`${exact}; 15 Sep 2026 12:00:00 GMT`);
  const store = new TvSessionCookieStore(memory.storage);
  assert.equal(await store.restore(), true);
  assert.equal(store.current, exact);
});

test('a concurrent clear prevents an older ensure from completing', async () => {
  const memory = memoryStorage();
  let failWrite = true;
  const started = deferred();
  const writeGate = deferred();
  memory.storage.setItem = async (_key, value) => {
    if (failWrite) throw new Error('disk unavailable');
    started.resolve();
    await writeGate.promise;
    memory.value = value;
  };
  const store = new TvSessionCookieStore(memory.storage);

  await store.capture(exact);
  failWrite = false;
  const ensuring = store.ensure();
  await started.promise;
  store.clear();
  writeGate.resolve();

  await assert.rejects(ensuring, /session changed while persisting/);
  await store.restore(); // drains the queued removal before reading again
  assert.equal(store.current, null);
  assert.equal(memory.value, null);
});

test('clear wins over an older restore and clear then capture stays ordered', async () => {
  const memory = memoryStorage(exact);
  const started = deferred();
  const readGate = deferred();
  memory.storage.getItem = async () => {
    started.resolve();
    await readGate.promise;
    return memory.value;
  };
  const store = new TvSessionCookieStore(memory.storage);

  const restoring = store.restore();
  await started.promise;
  store.clear();
  readGate.resolve();
  assert.equal(await restoring, false);
  await store.restore();
  assert.equal(store.current, null);

  memory.operations.length = 0;
  store.clear();
  const replacement = `NubArca.TvSession=${'b'.repeat(43)}`;
  await store.capture(replacement);
  await store.ensure();
  assert.deepEqual(memory.operations, ['remove', 'set']);
  assert.equal(memory.value, replacement);
});

test('a snapshot detects a clear queued after ensure resolves', async () => {
  const store = new TvSessionCookieStore(memoryStorage().storage);
  await store.capture(exact);

  const ensuring = store.ensure();
  queueMicrotask(() => store.clear());
  const generation = await ensuring;

  assert.equal(store.isCurrent(generation), false);
});

test('pairing validates and persists the exact manual cookie before entering the app', () => {
  const client = readFileSync(resolve(apiDir, 'client.ts'), 'utf8');
  const pairing = readFileSync(resolve(apiDir, '../screens/PairingScreen.tsx'), 'utf8');
  const probe = readFileSync(resolve(apiDir, '../video/probe.ts'), 'utf8');

  assert.doesNotMatch(client, /\.split\(','\)/);
  assert.match(client, /await captureCookie\(res\.headers\.get\('set-cookie'\)\)/);
  assert.match(client, /new TvSessionCookieStore\(AsyncStorage\)/);
  assert.equal((client.match(/credentials: 'omit'/g) ?? []).length, 2);
  assert.match(probe, /credentials: 'omit'/);

  assert.match(
    pairing,
    /const session = await withTimeout\([\s\S]*getTvSession\(signal\)[\s\S]*const stillPersisted = await ensureSessionPersisted\(\);[\s\S]*stillPersisted\(\)[\s\S]*onPaired\(session\)/,
  );
  assert.match(pairing, /error instanceof ApiError && error\.status === 401/);
  assert.match(pairing, /timedRequest\(startTvPairing\)/);
  assert.match(pairing, /startController\.current\?\.abort\(\)/);
  assert.match(pairing, /new AbortController\(\)/);
  assert.match(pairing, /activeRequest\?\.abort\(\)/);
  assert.match(pairing, /timer = setTimeout\(poll, 2000\)/);
});

// Image-loader unit tests: LRU caching, in-flight dedup, retry policy,
// logout generation guard, byte-bounded eviction and per-attempt timeout —
// all exercised through a stubbed global fetch (no network, no RN runtime).

import assert from 'node:assert/strict';
import { test, beforeEach, afterEach } from 'node:test';
import {
  loadImage,
  clearImageCache,
  getImageStats,
  hasSession,
  __testReset,
  __testConfigureLimits,
} from './imageLoader.ts';

import {
  setSessionCookieSource,
  staticSessionCookieSource,
} from '../api/sessionAccess.ts';
import { configureBaseUrl } from '../api/client.ts';

interface FakeBlob {
  size: number;
  arrayBuffer: () => Promise<ArrayBuffer>;
}

function fakeBlob(bytes: number): FakeBlob {
  return { size: bytes, arrayBuffer: async () => new ArrayBuffer(bytes) };
}

interface StubResponse {
  ok: boolean;
  status: number;
  blob: () => Promise<FakeBlob>;
}

type FetchStub = (
  url: string,
  init?: { signal?: AbortSignal },
) => Promise<StubResponse>;

let originalFetch: typeof globalThis.fetch | undefined;

function installFetch(stub: FetchStub): void {
  originalFetch = globalThis.fetch;
  (globalThis as { fetch: unknown }).fetch = stub as unknown as typeof fetch;
}

function restoreFetch(): void {
  if (originalFetch !== undefined) {
    globalThis.fetch = originalFetch;
    originalFetch = undefined;
  }
}

function okResponse(bytes: number): Promise<StubResponse> {
  return Promise.resolve({ ok: true, status: 200, blob: async () => fakeBlob(bytes) });
}

beforeEach(() => {
  __testReset();
  __testConfigureLimits({
    entries: 250,
    totalBytes: 48 * 1024 * 1024,
    timeoutMs: 30_000,
  });
  setSessionCookieSource(staticSessionCookieSource('NubArca.Auth=tok'));
  configureBaseUrl('https://unit.test');
});

afterEach(() => {
  restoreFetch();
});

test('signed-out loads fail closed with 401 and never hit fetch', async () => {
  let fetched = 0;
  installFetch(() => {
    fetched += 1;
    return okResponse(10);
  });
  setSessionCookieSource(staticSessionCookieSource(null));
  await assert.rejects(loadImage('/p/1'), (err: { status?: number }) => err.status === 401);
  assert.equal(fetched, 0);
  assert.equal(hasSession(), false);
});

test('second load of the same path is a cache hit (one fetch)', async () => {
  let fetched = 0;
  installFetch(() => {
    fetched += 1;
    return okResponse(12);
  });
  const first = await loadImage('/p/a');
  const second = await loadImage('/p/a');
  assert.equal(first, second);
  assert.equal(fetched, 1);
  const s = getImageStats();
  assert.equal(s.hits, 1);
  assert.equal(s.fetches, 1);
  assert.ok(s.totalBytes > 0);
});

test('concurrent loads of one path share a single fetch', async () => {
  let fetched = 0;
  installFetch(() => {
    fetched += 1;
    return new Promise((resolve) =>
      setTimeout(() => resolve({ ok: true, status: 200, blob: async () => fakeBlob(8) }), 10),
    );
  });
  const [a, b] = await Promise.all([loadImage('/p/x'), loadImage('/p/x')]);
  assert.equal(a, b);
  assert.equal(fetched, 1);
});

test('transient 500 is retried up to MAX_ATTEMPTS then succeeds', async () => {
  let fetched = 0;
  installFetch(() => {
    fetched += 1;
    if (fetched < 3) {
      return Promise.resolve({ ok: false, status: 500, blob: async () => fakeBlob(0) });
    }
    return okResponse(20);
  });
  const uri = await loadImage('/p/retry');
  assert.ok(uri.startsWith('data:image/jpeg;base64,'));
  assert.equal(fetched, 3);
});

test('permanent 404 is never retried', async () => {
  let fetched = 0;
  installFetch(() => {
    fetched += 1;
    return Promise.resolve({ ok: false, status: 404, blob: async () => fakeBlob(0) });
  });
  await assert.rejects(loadImage('/p/gone'));
  assert.equal(fetched, 1);
});

test('logout generation discards an in-flight result', async () => {
  let resolveFetch!: (r: StubResponse) => void;
  installFetch(
    () =>
      new Promise<StubResponse>((resolve) => {
        resolveFetch = resolve;
      }),
  );
  const pending = loadImage('/p/late');
  // Let the loader reach its fetch call first (the semaphore hop is a
  // microtask; give it a full macrotask so the stub is definitely engaged).
  await new Promise((resolve) => setImmediate(resolve));
  clearImageCache(); // session torn down while bytes are in flight
  resolveFetch({ ok: true, status: 200, blob: async () => fakeBlob(16) });
  // The promise still settles (the caller's screen ignores it via its own
  // cancelled flag), but NOTHING may land in the post-logout cache.
  await pending;
  assert.equal(getImageStats().cached, 0);
  assert.equal(getImageStats().totalBytes, 0);
  // A later identical load refetches: no resurrection from the dead run.
  let fetchedAfter = 0;
  installFetch(() => {
    fetchedAfter += 1;
    return okResponse(16);
  });
  await loadImage('/p/late');
  assert.equal(fetchedAfter, 1);
  assert.equal(getImageStats().cached, 1);
});

test('byte-bounded eviction drops oldest entries past the ceiling', async () => {
  // Each 300-byte wire payload stores ≈400 data-URI bytes; ceiling 900 fits
  // exactly two entries, so the third load must evict the first.
  __testConfigureLimits({ entries: 10, totalBytes: 900 });
  let fetched = 0;
  installFetch(() => {
    fetched += 1;
    return okResponse(300 - fetched * 0);
  });
  await loadImage('/p/1');
  await loadImage('/p/2');
  await loadImage('/p/3');
  const s = getImageStats();
  assert.ok(s.evictions >= 1, `expected evictions, got ${s.evictions}`);
  assert.ok(s.totalBytes <= 900, `totalBytes ${s.totalBytes} exceeds 900`);
});

test('a hung connection is aborted by the per-attempt timeout', async () => {
  __testConfigureLimits({ timeoutMs: 60 });
  let aborted = 0;
  installFetch((_url, init) => {
    return new Promise((_, reject) => {
      init?.signal?.addEventListener('abort', () => {
        aborted += 1;
        reject(new Error('aborted by test'));
      });
    });
  });
  await assert.rejects(loadImage('/p/hang'), /timed out|aborted/i);
  // 3 attempts were all cut off by the timeout instead of hanging forever.
  assert.equal(aborted, 3);
});

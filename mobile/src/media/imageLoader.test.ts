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

interface ControlledRequest {
  url: string;
  respond: (status?: number, bytes?: number) => void;
}

interface ControlledFetch {
  pending: ControlledRequest[];
  active: number;
  maxActive: number;
}

function installControlledFetch(): ControlledFetch {
  const state: ControlledFetch = { pending: [], active: 0, maxActive: 0 };
  installFetch((url, init) =>
    new Promise<StubResponse>((resolve, reject) => {
      state.active += 1;
      state.maxActive = Math.max(state.maxActive, state.active);
      let settled = false;

      const settle = (result: () => void) => {
        if (settled) return;
        settled = true;
        state.active -= 1;
        result();
      };
      init?.signal?.addEventListener('abort', () =>
        settle(() => reject(new Error('aborted by controlled fetch'))),
      );
      state.pending.push({
        url,
        respond: (status = 200, bytes = 8) =>
          settle(() =>
            resolve({
              ok: status >= 200 && status < 300,
              status,
              blob: async () => fakeBlob(bytes),
            }),
          ),
      });
    }),
  );
  return state;
}

async function waitFor(
  condition: () => boolean,
  message: string,
  timeoutMs = 750,
): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (!condition()) {
    if (Date.now() >= deadline) throw new Error(message);
    await new Promise((resolve) => setImmediate(resolve));
  }
}

async function within<T>(promise: Promise<T>, message: string, timeoutMs = 750): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), timeoutMs);
      }),
    ]);
  } finally {
    if (timer !== undefined) clearTimeout(timer);
  }
}

function respondToPending(
  state: ControlledFetch,
  responseFor: (request: ControlledRequest) => number = () => 200,
): void {
  const wave = state.pending.splice(0);
  for (const request of wave) request.respond(responseFor(request));
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

test('queued image loads transfer permits and remain live after a full drain', async () => {
  const controlled = installControlledFetch();
  const batch = Array.from({ length: 12 }, (_, index) => loadImage(`/p/live-${index}`));

  await waitFor(
    () => controlled.pending.length === 6,
    'the first six image requests did not acquire their slots',
  );
  assert.equal(getImageStats().active, 6);
  assert.equal(getImageStats().queued, 6);

  respondToPending(controlled);
  await waitFor(
    () => controlled.pending.length === 6,
    'queued image requests did not receive transferred slots',
  );
  respondToPending(controlled);
  await within(Promise.all(batch), 'the queued image batch did not drain');

  const afterBatch = getImageStats();
  const thirteenth = loadImage('/p/live-after-drain');
  await waitFor(
    () => controlled.pending.length === 1,
    'the request after the drained batch never acquired a slot',
  );
  respondToPending(controlled);
  await within(thirteenth, 'the request after the drained batch never completed');

  assert.ok(controlled.maxActive <= 6, `observed ${controlled.maxActive} concurrent fetches`);
  assert.equal(afterBatch.active, 0);
  assert.equal(afterBatch.queued, 0);
  assert.equal(afterBatch.inFlight, 0);
  assert.equal(getImageStats().active, 0);
  assert.equal(getImageStats().queued, 0);
  assert.equal(getImageStats().inFlight, 0);
});

test('repeated queued bursts return the image loader to idle after every drain', async () => {
  const controlled = installControlledFetch();

  for (let burst = 0; burst < 3; burst += 1) {
    const batch = Array.from({ length: 12 }, (_, index) =>
      loadImage(`/p/burst-${burst}-${index}`),
    );
    await waitFor(
      () => controlled.pending.length === 6,
      `burst ${burst + 1} did not start its first wave`,
    );
    respondToPending(controlled);
    await waitFor(
      () => controlled.pending.length === 6,
      `burst ${burst + 1} did not advance its queued wave`,
    );
    respondToPending(controlled);
    await within(Promise.all(batch), `burst ${burst + 1} did not drain`);

    const stats = getImageStats();
    assert.equal(stats.active, 0, `burst ${burst + 1} leaked active permits`);
    assert.equal(stats.queued, 0, `burst ${burst + 1} leaked queued waiters`);
    assert.equal(stats.inFlight, 0, `burst ${burst + 1} leaked in-flight loads`);
  }
  assert.ok(controlled.maxActive <= 6, `observed ${controlled.maxActive} concurrent fetches`);
});

test('a permanently failing queued request releases its slot and the queue stays live', async () => {
  const controlled = installControlledFetch();
  const failingPath = '/p/queued-failure';
  const paths = [
    ...Array.from({ length: 11 }, (_, index) => `/p/failure-batch-${index}`),
    failingPath,
  ];
  const batch = paths.map((path) => loadImage(path));

  await waitFor(() => controlled.pending.length === 6, 'failure batch did not start');
  respondToPending(controlled);
  await waitFor(() => controlled.pending.length === 6, 'failure batch queue did not advance');
  respondToPending(controlled, (request) => (request.url.endsWith(failingPath) ? 404 : 200));

  const results = await within(Promise.allSettled(batch), 'failure batch did not settle');
  assert.equal(results.filter((result) => result.status === 'rejected').length, 1);
  assert.equal(results.at(-1)?.status, 'rejected');
  assert.equal(getImageStats().active, 0);
  assert.equal(getImageStats().queued, 0);
  assert.equal(getImageStats().inFlight, 0);

  const afterFailure = loadImage('/p/after-queued-failure');
  await waitFor(
    () => controlled.pending.length === 1,
    'a request after the queued failure never acquired a slot',
  );
  respondToPending(controlled);
  await within(afterFailure, 'a request after the queued failure never completed');
  assert.ok(controlled.maxActive <= 6, `observed ${controlled.maxActive} concurrent fetches`);
  assert.equal(getImageStats().active, 0);
});

test('logout generation isolation does not corrupt active or queued permits', async () => {
  const controlled = installControlledFetch();
  const oldGeneration = Array.from({ length: 12 }, (_, index) =>
    loadImage(`/p/pre-logout-${index}`),
  );

  await waitFor(() => controlled.pending.length === 6, 'pre-logout batch did not start');
  assert.equal(getImageStats().queued, 6);
  clearImageCache();
  respondToPending(controlled);
  await waitFor(
    () => controlled.pending.length === 6,
    'pre-logout queued requests did not receive transferred slots',
  );
  respondToPending(controlled);
  await within(Promise.all(oldGeneration), 'pre-logout generation did not drain');

  const afterOldGeneration = getImageStats();
  assert.equal(afterOldGeneration.cached, 0);
  assert.equal(afterOldGeneration.totalBytes, 0);
  assert.equal(afterOldGeneration.active, 0);
  assert.equal(afterOldGeneration.queued, 0);
  assert.equal(afterOldGeneration.inFlight, 0);

  const newGeneration = loadImage('/p/post-logout');
  await waitFor(
    () => controlled.pending.length === 1,
    'the post-logout generation never acquired a slot',
  );
  respondToPending(controlled);
  await within(newGeneration, 'the post-logout generation never completed');
  assert.equal(getImageStats().cached, 1);
  assert.equal(getImageStats().active, 0);
  assert.equal(getImageStats().queued, 0);
  assert.equal(getImageStats().inFlight, 0);
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

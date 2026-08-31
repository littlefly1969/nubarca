// Mobile video preflight probe tests: Range-bounded, cookie-carrying,
// aborted-at-head probing over the CANONICAL delivery contract.
//
// The classification itself — and the fact that it is identical to web's and
// TV's — is proven in videoDeliveryParity.test.ts against the shared fixture.
// THIS file covers the React-Native adapter around it: what goes on the wire,
// when the transfer is aborted, how the head is read, how the shared retry
// policy is executed, and what cancellation does.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  createManagedProbe,
  probeVideoSource,
  resolveExpoVideoSource,
  type VideoProbeFetch,
  type VideoProbeOutcome,
} from './videoProbe.ts';

const SRC = {
  uri: 'https://unit.test/api/files/own-1/video',
  headers: { cookie: 'NubArca.Auth=tok' },
};

/** Let one macrotask pass so an attempt can actually start/settle. */
function settle(): Promise<void> {
  return new Promise((done) => setImmediate(done));
}

interface RecordedRequest {
  url: string;
  cookie: string | null;
  range: string | null;
  signal: AbortSignal;
}

function makeRecorder(
  responses: Array<{ status: number; contentType?: string; retryAfter?: string }>,
): { fetch: VideoProbeFetch; requests: RecordedRequest[] } {
  const requests: RecordedRequest[] = [];
  let index = 0;
  const fetch: VideoProbeFetch = (uri, init) => {
    const signal = init.signal;
    requests.push({
      url: uri,
      cookie: init.headers.cookie ?? null,
      range: init.headers.range ?? null,
      signal,
    });
    const response = responses[Math.min(index, responses.length - 1)];
    index += 1;
    return Promise.resolve({
      status: response.status,
      headers: {
        get: (n: string) => {
          const name = n.toLowerCase();
          if (name === 'content-type') return response.contentType ?? null;
          if (name === 'retry-after') return response.retryAfter ?? null;
          return null;
        },
      },
    });
  };
  return { fetch, requests };
}

/** A sleep that records the canonical delays instead of waiting them out. */
function recordingSleep(): { sleep: (ms: number) => Promise<void>; delays: number[] } {
  const delays: number[] = [];
  return {
    delays,
    sleep: (ms: number) => {
      delays.push(ms);
      return Promise.resolve();
    },
  };
}

// ── The wire ─────────────────────────────────────────────────────────────────

test('owned progressive: sends Range bytes=0-0 + Cookie and reads 206 as progressive', async () => {
  const { fetch, requests } = makeRecorder([{ status: 206, contentType: 'video/mp4' }]);
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.deepEqual(outcome, { kind: 'ready', mode: 'progressive' });
  assert.equal(requests.length, 1);
  assert.equal(requests[0].url, SRC.uri);
  assert.equal(requests[0].cookie, 'NubArca.Auth=tok');
  assert.equal(requests[0].range, 'bytes=0-0');
});

test('the probe aborts the transfer as soon as the response head is known', async () => {
  const { fetch, requests } = makeRecorder([{ status: 206, contentType: 'video/mp4' }]);
  await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.equal(requests[0].signal.aborted, true);
});

test('reads the response head before abort invalidates a native header accessor', async () => {
  // Physical-Android regression: React Native owns the Response and may
  // release its native header accessor the moment the request is aborted.
  // Aborting first turned a real HLS master into a typeless 200.
  let aborted = false;
  const fetch: VideoProbeFetch = (_uri, init) => {
    init.signal.addEventListener('abort', () => { aborted = true; });
    return Promise.resolve({
      status: 200,
      headers: {
        get: (n: string) =>
          aborted
            ? null
            : n.toLowerCase() === 'content-type'
              ? 'application/vnd.apple.mpegurl'
              : null,
      },
    });
  };
  assert.deepEqual(await probeVideoSource(SRC, { fetchImpl: fetch }), {
    kind: 'ready',
    mode: 'hls',
  });
});

test('200 + the NubArca HLS MIME (with or without parameters) is HLS', async () => {
  for (const contentType of [
    'application/vnd.apple.mpegurl',
    'application/vnd.apple.mpegurl; charset=utf-8',
    'APPLICATION/VND.APPLE.MPEGURL',
  ]) {
    const { fetch } = makeRecorder([{ status: 200, contentType }]);
    assert.deepEqual(
      await probeVideoSource(SRC, { fetchImpl: fetch }),
      { kind: 'ready', mode: 'hls' },
      contentType,
    );
  }
});

test('every 200/206 head the server can send is playable, MIME or not', async () => {
  // The mobile bug this slice closes. None of these may be "unavailable".
  for (const status of [200, 206]) {
    for (const contentType of [undefined, 'video/mp4', 'video/quicktime', 'application/octet-stream']) {
      const { fetch } = makeRecorder([{ status, contentType }]);
      assert.deepEqual(
        await probeVideoSource(SRC, { fetchImpl: fetch }),
        { kind: 'ready', mode: 'progressive' },
        `${status} + ${contentType ?? 'no content-type'}`,
      );
    }
  }
});

// ── The shared retry policy, executed ────────────────────────────────────────

test('202 -> 202 -> ready follows the canonical backoff and surfaces preparing', async () => {
  const { fetch } = makeRecorder([
    { status: 202 },
    { status: 202 },
    { status: 200, contentType: 'application/vnd.apple.mpegurl' },
  ]);
  const { sleep, delays } = recordingSleep();
  let preparing = 0;
  const outcome = await probeVideoSource(SRC, {
    fetchImpl: fetch,
    sleepImpl: sleep,
    onPreparing: () => { preparing += 1; },
  });
  assert.deepEqual(outcome, { kind: 'ready', mode: 'hls' });
  assert.deepEqual(delays, [1500, 2500]);
  assert.equal(preparing, 2);
});

test('Retry-After is a FLOOR over the canonical ramp, never a replacement for it', async () => {
  const { fetch } = makeRecorder([
    { status: 202, retryAfter: '2' }, // 2000 > ramp step 1500 → wins
    { status: 202, retryAfter: '2' }, // 2000 < ramp step 2500 → ramp wins
    { status: 206, contentType: 'video/mp4' },
  ]);
  const { sleep, delays } = recordingSleep();
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch, sleepImpl: sleep });
  assert.deepEqual(outcome, { kind: 'ready', mode: 'progressive' });
  assert.deepEqual(delays, [2000, 2500]);
});

test('a long preparation is NOT capped at ten attempts', async () => {
  // The removed mobile-only ceiling: a healthy but slow transcode used to be
  // reported as an error purely because a counter ran out.
  let calls = 0;
  const fetch: VideoProbeFetch = () => {
    calls += 1;
    if (calls <= 40) return Promise.resolve({ status: 202, headers: { get: () => null } });
    return Promise.resolve({
      status: 206,
      headers: { get: (n: string) => (n.toLowerCase() === 'content-type' ? 'video/mp4' : null) },
    });
  };
  const { sleep, delays } = recordingSleep();
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch, sleepImpl: sleep });
  assert.deepEqual(outcome, { kind: 'ready', mode: 'progressive' });
  assert.equal(calls, 41);
  assert.equal(delays.length, 40);
  assert.equal(delays[delays.length - 1], 5000); // the ramp caps, the loop does not
});

test('404 is a distinct terminal verdict: ONE call, no retries', async () => {
  const { fetch, requests } = makeRecorder([{ status: 404 }]);
  assert.deepEqual(await probeVideoSource(SRC, { fetchImpl: fetch }), { kind: 'not-found' });
  assert.equal(requests.length, 1);
});

test('auth failures are their own verdict and are not retried', async () => {
  for (const status of [401, 403]) {
    const { fetch, requests } = makeRecorder([{ status }]);
    assert.deepEqual(
      await probeVideoSource(SRC, { fetchImpl: fetch }),
      { kind: 'auth-error' },
      String(status),
    );
    assert.equal(requests.length, 1);
  }
});

test('temporary server failures retry and can recover instead of becoming unavailable', async () => {
  const { fetch, requests } = makeRecorder([
    { status: 503 },
    { status: 429 },
    { status: 206, contentType: 'video/mp4' },
  ]);
  const { sleep } = recordingSleep();
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch, sleepImpl: sleep });
  assert.equal(requests.length, 3);
  assert.deepEqual(outcome, { kind: 'ready', mode: 'progressive' });
});

test('an exhausted transient budget is transient-error, never false unavailability', async () => {
  const networkDown: VideoProbeFetch = async () => {
    throw new Error('network down');
  };
  const { sleep, delays } = recordingSleep();
  assert.deepEqual(
    await probeVideoSource(SRC, { fetchImpl: networkDown, sleepImpl: sleep }),
    { kind: 'transient-error' },
  );
  // Bounded, unlike preparing: 3 retries on the shared ramp, then it settles.
  assert.deepEqual(delays, [1500, 2500, 5000]);

  const { fetch } = makeRecorder([{ status: 500 }]);
  assert.deepEqual(
    await probeVideoSource(SRC, { fetchImpl: fetch, sleepImpl: recordingSleep().sleep }),
    { kind: 'transient-error' },
  );
});

test('an unexpected status is a protocol error, not a missing file', async () => {
  const { fetch, requests } = makeRecorder([{ status: 418 }]);
  assert.deepEqual(await probeVideoSource(SRC, { fetchImpl: fetch }), {
    kind: 'protocol-error',
  });
  assert.equal(requests.length, 1);
});

// ── Resolution ───────────────────────────────────────────────────────────────

test('resolve: progressive DECLARES contentType progressive on the probed URL', () => {
  // ExoPlayer cannot infer a container from an extension-less /video URL, so
  // omitting the hint left it guessing on exactly the sources already
  // identified by the probe.
  const resolved = resolveExpoVideoSource(SRC, { kind: 'ready', mode: 'progressive' });
  assert.deepEqual(resolved, {
    uri: SRC.uri,
    headers: { cookie: 'NubArca.Auth=tok' },
    contentType: 'progressive',
  });
});

test('resolve: HLS declares contentType hls on the SAME probed URL', () => {
  const resolved = resolveExpoVideoSource(SRC, { kind: 'ready', mode: 'hls' });
  assert.deepEqual(resolved, {
    uri: SRC.uri,
    headers: { cookie: 'NubArca.Auth=tok' },
    contentType: 'hls',
  });
});

test('resolve: shared media plays the server-provided URL, never an owner route', async () => {
  const sharedUrl = 'https://unit.test/api/shared-albums/shr-1/media/f-7/video';
  const base = { uri: sharedUrl, headers: { cookie: 'NubArca.Auth=tok' } };
  for (const [contentType, mode] of [
    ['application/vnd.apple.mpegurl', 'hls'],
    ['video/mp4', 'progressive'],
  ] as const) {
    const { fetch } = makeRecorder([{ status: 200, contentType }]);
    const outcome: VideoProbeOutcome = await probeVideoSource(base, { fetchImpl: fetch });
    const resolved = resolveExpoVideoSource(base, outcome);
    assert.ok(resolved !== null);
    assert.equal(resolved.contentType, mode);
    assert.equal(resolved.uri, sharedUrl);
    assert.equal(resolved.headers.cookie, 'NubArca.Auth=tok');
    assert.ok(
      !resolved.uri.includes('/api/files/'),
      'shared playback must never touch the owner-only family',
    );
  }
});

test('resolve: non-ready outcomes mount NOTHING', () => {
  for (const outcome of [
    { kind: 'preparing', retryAfterMs: null },
    { kind: 'not-found' },
    { kind: 'auth-error' },
    { kind: 'transient-error' },
    { kind: 'protocol-error' },
    { kind: 'cancelled' },
  ] as const) {
    assert.equal(resolveExpoVideoSource(SRC, outcome), null, outcome.kind);
  }
});

// ── Time bounds & cancellation ───────────────────────────────────────────────

test('hung fetch: EVERY attempt is time-bounded and the transient budget still holds', async () => {
  let calls = 0;
  const attemptSignals: AbortSignal[] = [];
  // The server NEVER answers; only the attempt timeout can end an attempt —
  // and the real fetch must be the one receiving the abort.
  const fetch: VideoProbeFetch = (_uri, init) =>
    new Promise((_resolve, reject) => {
      calls += 1;
      attemptSignals.push(init.signal);
      init.signal.addEventListener('abort', () =>
        reject(new Error('This operation was aborted')),
      );
    });

  const outcome = await probeVideoSource(SRC, {
    fetchImpl: fetch,
    attemptTimeoutMs: 20,
    sleepImpl: recordingSleep().sleep,
  });

  // Settles at all → the probe cannot hang indefinitely.
  assert.deepEqual(outcome, { kind: 'transient-error' });
  // A timeout is ONE transient failure: the shared budget was followed.
  assert.equal(calls, 1 + 3);
  for (const signal of attemptSignals) {
    assert.equal(signal.aborted, true); // the FETCH got aborted, not a race
  }
});

test('caller abort stops the ACTIVE request and no further attempt starts', async () => {
  const caller = new AbortController();
  let calls = 0;
  let activeSignal: AbortSignal | null = null;
  const fetch: VideoProbeFetch = (_uri, init) =>
    new Promise((_resolve, reject) => {
      calls += 1;
      activeSignal = init.signal;
      init.signal.addEventListener('abort', () =>
        reject(new Error('This operation was aborted')),
      );
    });

  const pending = probeVideoSource(SRC, { fetchImpl: fetch, signal: caller.signal });
  await settle(); // attempt 1 is now in flight
  assert.notEqual(activeSignal, null);
  assert.equal(activeSignal!.aborted, false);

  const started = Date.now();
  caller.abort(); // "unmount"
  const outcome = await pending;

  assert.ok(Date.now() - started < 1500); // stopped PROMPTLY, no backoff wait
  assert.deepEqual(outcome, { kind: 'cancelled' }); // never a false verdict
  assert.equal(activeSignal!.aborted, true); // active fetch signalled aborted
  assert.equal(calls, 1); // no subsequent attempt
});

test('caller abort during the RETRY DELAY ends the wait and skips attempt N+1', async () => {
  const caller = new AbortController();
  let calls = 0;
  // Transport failure on attempt 1 → the probe enters the pre-retry delay…
  const fetch: VideoProbeFetch = async () => {
    calls += 1;
    throw new Error('network down');
  };

  const started = Date.now();
  const pending = probeVideoSource(SRC, { fetchImpl: fetch, signal: caller.signal });
  await settle(); // attempt 1 failed; the probe is now sleeping its 1.5 s
  caller.abort();

  const outcome = await pending;
  assert.ok(Date.now() - started < 1500); // the delay terminated immediately
  assert.deepEqual(outcome, { kind: 'cancelled' });
  assert.equal(calls, 1); // attempt N+1 never started
});

test('a probe started after the caller is already gone never touches the network', async () => {
  const caller = new AbortController();
  caller.abort();
  const { fetch, requests } = makeRecorder([{ status: 206, contentType: 'video/mp4' }]);
  assert.deepEqual(
    await probeVideoSource(SRC, { fetchImpl: fetch, signal: caller.signal }),
    { kind: 'cancelled' },
  );
  assert.equal(requests.length, 0);
});

test('createManagedProbe.cancel kills the live attempt exactly like cleanup must', async () => {
  let activeSignal: AbortSignal | null = null;
  const fetch: VideoProbeFetch = (_uri, init) =>
    new Promise((_resolve, reject) => {
      activeSignal = init.signal;
      init.signal.addEventListener('abort', () =>
        reject(new Error('This operation was aborted')),
      );
    });

  const probe = createManagedProbe(SRC, { fetchImpl: fetch });
  await settle();
  assert.notEqual(activeSignal, null);

  probe.cancel(); // what VideoSlide's effect cleanup does
  const outcome = await probe.outcome;
  assert.deepEqual(outcome, { kind: 'cancelled' }); // settles; safe to ignore
  assert.equal(activeSignal!.aborted, true); // the request itself was cancelled
});

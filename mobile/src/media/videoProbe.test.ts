// Video preflight probe tests (acceptance contract fix): Range-bounded,
// cookie-carrying, aborted-at-head probing with NubArca's real classification
// — 202 preparing / 404 unavailable / 206+video progressive / 200 HLS MIME.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  classifyVideoProbe,
  createManagedProbe,
  probeVideoSource,
  resolveExpoVideoSource,
  type VideoProbeFetch,
  type VideoProbeOutcome,
  type VideoProbePhase,
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
  responses: Array<{ status: number; contentType?: string }>,
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
        get: (n: string) =>
          n.toLowerCase() === 'content-type' ? (response.contentType ?? null) : null,
      },
    });
  };
  return { fetch, requests };
}

test('owned progressive: sends Range bytes=0-0 + Cookie, classifies 206 video/* as progressive', async () => {
  const { fetch, requests } = makeRecorder([{ status: 206, contentType: 'video/mp4' }]);
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.deepEqual(outcome, { phase: 'ready', container: 'progressive' });

  assert.equal(requests.length, 1);
  assert.equal(requests[0].range, 'bytes=0-0');
  assert.equal(requests[0].cookie, 'NubArca.Auth=tok');
});

test('the probe aborts the transfer as soon as the response head is known', async () => {
  const { fetch, requests } = makeRecorder([{ status: 206, contentType: 'video/mp4' }]);
  await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.equal(requests[0].signal.aborted, true);
});

test('200 + NubArca HLS MIME classifies as HLS', async () => {
  const { fetch } = makeRecorder([
    { status: 200, contentType: 'application/vnd.apple.mpegurl' },
  ]);
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.deepEqual(outcome, { phase: 'ready', container: 'hls' });
});

test('202 keeps the loop alive within the bounded budget', async () => {
  let calls = 0;
  const fetch: VideoProbeFetch = () => {
    calls += 1;
    if (calls < 3) {
      return Promise.resolve({ status: 202, headers: { get: () => null } });
    }
    return Promise.resolve({
      status: 200,
      headers: {
        get: (n) =>
          n.toLowerCase() === 'content-type' ? 'application/vnd.apple.mpegurl' : null,
      },
    });
  };
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch, retryMs: 1 });
  assert.equal(calls, 3);
  assert.deepEqual(outcome, { phase: 'ready', container: 'hls' });
});

test('200 + HLS MIME WITH parameters (charset=utf-8) still classifies as HLS', async () => {
  // Merge-blocker regression: ASP.NET Core serves the master through
  // Results.Text, which may materialize the declared type with an explicit
  // charset. An exact-match comparison turned that READY answer into
  // "unavailable" on a real server.
  const { fetch } = makeRecorder([
    { status: 200, contentType: 'application/vnd.apple.mpegurl; charset=utf-8' },
  ]);
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.deepEqual(outcome, { phase: 'ready', container: 'hls' });
  // Parameter-stripping keeps case-insensitivity of the bare type.
  assert.deepEqual(
    classifyVideoProbe(200, 'APPLICATION/VND.APPLE.MPEGURL; CHARSET=UTF-8'),
    { phase: 'ready', container: 'hls' },
  );
});

test('every retried 202 surfaces preparing through onPhase BEFORE the final verdict', async () => {
  const seen: VideoProbePhase[] = [];
  const { fetch } = makeRecorder([
    { status: 202 },
    { status: 202 },
    { status: 200, contentType: 'application/vnd.apple.mpegurl' },
  ]);
  const outcome = await probeVideoSource(SRC, {
    fetchImpl: fetch,
    retryMs: 1,
    onPhase: (phase) => seen.push(phase),
  });
  assert.deepEqual(seen, ['preparing', 'preparing']);
  assert.deepEqual(outcome, { phase: 'ready', container: 'hls' });
});

test('the budget-exhausting 202 and terminal outcomes never pass through onPhase', async () => {
  const seen: VideoProbePhase[] = [];
  const { fetch } = makeRecorder([{ status: 202 }]);
  const outcome = await probeVideoSource(SRC, {
    fetchImpl: fetch,
    maxAttempts: 3,
    retryMs: 1,
    onPhase: (phase) => seen.push(phase),
  });
  // The two RETRIED 202s were notified; the LAST one resolves straight to
  // retryable error without a pointless preparing flash right before the end.
  assert.deepEqual(seen, ['preparing', 'preparing']);
  assert.equal(outcome.phase, 'error');
});

test('404 is deliberate unavailability: ONE call, no retries', async () => {
  let calls = 0;
  const fetch: VideoProbeFetch = () => {
    calls += 1;
    return Promise.resolve({ status: 404, headers: { get: () => null } });
  };
  const outcome = await probeVideoSource(SRC, { fetchImpl: fetch });
  assert.equal(outcome.phase, 'unavailable');
  assert.equal(calls, 1);
});

test('temporary server failures retry and can recover instead of becoming unavailable', async () => {
  const { fetch, requests } = makeRecorder([
    { status: 503 },
    { status: 429 },
    { status: 206, contentType: 'video/mp4' },
  ]);
  const outcome = await probeVideoSource(SRC, {
    fetchImpl: fetch,
    retryMs: 1,
    maxAttempts: 3,
  });
  assert.equal(requests.length, 3);
  assert.deepEqual(outcome, { phase: 'ready', container: 'progressive' });
});

test('an exhausted transport/preparation budget is retryable error, never false unavailability', async () => {
  const networkDown: VideoProbeFetch = async () => {
    throw new Error('network down');
  };
  assert.deepEqual(
    await probeVideoSource(SRC, {
      fetchImpl: networkDown,
      retryMs: 1,
      maxAttempts: 2,
    }),
    { phase: 'error' },
  );

  const { fetch } = makeRecorder([{ status: 202 }]);
  assert.deepEqual(
    await probeVideoSource(SRC, { fetchImpl: fetch, retryMs: 1, maxAttempts: 2 }),
    { phase: 'error' },
  );
});

// ── Resolution ───────────────────────────────────────────────────────────────

test('resolve: progressive outcome yields a NATIVE source without contentType', () => {
  const resolved = resolveExpoVideoSource(SRC, {
    phase: 'ready',
    container: 'progressive',
  });
  assert.ok(resolved !== null);
  assert.equal(resolved.uri, SRC.uri);
  assert.equal(resolved.headers.cookie, 'NubArca.Auth=tok');
  assert.equal('contentType' in resolved, false);
});

test('resolve: HLS outcome declares contentType hls on the SAME probed URL', () => {
  const resolved = resolveExpoVideoSource(SRC, { phase: 'ready', container: 'hls' });
  assert.ok(resolved !== null);
  assert.equal(resolved.uri, SRC.uri);
  assert.equal(resolved.contentType, 'hls');
  // Never synthesizes a different media URL for a probed source.
  assert.ok(!resolved.uri.includes('/api/shared-albums/'));
});

test('resolve: shared HLS stays HLS using ONLY the server-provided URL', async () => {
  const sharedUrl = 'https://unit.test/api/shared-albums/shr-1/media/f-7/video';
  const { fetch } = makeRecorder([
    { status: 200, contentType: 'application/vnd.apple.mpegurl' },
  ]);
  const outcome: VideoProbeOutcome = await probeVideoSource(
    { uri: sharedUrl, headers: { cookie: 'NubArca.Auth=tok' } },
    { fetchImpl: fetch },
  );
  const resolved = resolveExpoVideoSource(
    { uri: sharedUrl, headers: { cookie: 'NubArca.Auth=tok' } },
    outcome,
  );
  assert.ok(resolved !== null);
  assert.equal(resolved.contentType, 'hls');
  assert.equal(resolved.uri, sharedUrl);
  assert.ok(
    !resolved.uri.includes('/api/files/'),
    'shared playback must never touch the owner-only family',
  );
});

test('resolve: non-ready outcomes mount NOTHING', () => {
  assert.equal(resolveExpoVideoSource(SRC, { phase: 'preparing' }), null);
  assert.equal(resolveExpoVideoSource(SRC, { phase: 'unavailable' }), null);
  assert.equal(resolveExpoVideoSource(SRC, { phase: 'error' }), null);
});

// ── classify unit corners ────────────────────────────────────────────────────

test('classify corners', () => {
  assert.deepEqual(classifyVideoProbe(202, null), { phase: 'preparing' });
  assert.deepEqual(classifyVideoProbe(404, null), { phase: 'unavailable' });
  assert.deepEqual(classifyVideoProbe(403, 'text/html'), { phase: 'error' });
  assert.deepEqual(classifyVideoProbe(503, null), {
    phase: 'error',
    retryable: true,
  });
  // A 206 whose content-type is NOT video is not playable through this route.
  assert.deepEqual(classifyVideoProbe(206, 'application/json'), {
    phase: 'unavailable',
  });
});

// ── Time bounds & cancellation (MOBILE-VIDEO-PROBE-LIFECYCLE-01) ────────────

test('hung fetch: EVERY attempt is time-bounded and the retry budget still holds', async () => {
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
    retryMs: 5,
    maxAttempts: 3,
  });

  // Settles at all → the probe cannot hang indefinitely.
  assert.equal(outcome.phase, 'error');
  // A timeout is ONE failed attempt: the configured budget was followed.
  assert.equal(calls, 3);
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

  const pending = probeVideoSource(SRC, {
    fetchImpl: fetch,
    signal: caller.signal,
    retryMs: 60_000, // would wait a full minute if cancellation were ignored
    maxAttempts: 5,
  });
  await settle(); // attempt 1 is now in flight
  assert.notEqual(activeSignal, null);
  assert.equal(activeSignal!.aborted, false);

  const started = Date.now();
  caller.abort(); // "unmount"
  const outcome = await pending;

  assert.ok(Date.now() - started < 60_000); // stopped PROMPTLY
  assert.equal(outcome.phase, 'unavailable');
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
  const pending = probeVideoSource(SRC, {
    fetchImpl: fetch,
    signal: caller.signal,
    retryMs: 30_000, // …a delay that must NOT be waited out after cancellation
    maxAttempts: 3,
  });
  await settle(); // attempt 1 failed; the probe is now sleeping
  caller.abort();

  const outcome = await pending;
  assert.ok(Date.now() - started < 30_000); // delay terminated immediately
  assert.equal(outcome.phase, 'unavailable');
  assert.equal(calls, 1); // attempt N+1 never started
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

  const probe = createManagedProbe(SRC, { fetchImpl: fetch, retryMs: 60_000 });
  await settle();
  assert.notEqual(activeSignal, null);

  probe.cancel(); // what VideoSlide's effect cleanup does
  const outcome = await probe.outcome;
  assert.equal(outcome.phase, 'unavailable'); // settles; safe to ignore
  assert.equal(activeSignal!.aborted, true); // the request itself was cancelled
});

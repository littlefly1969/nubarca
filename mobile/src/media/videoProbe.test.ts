// Video preflight probe tests (acceptance contract fix): Range-bounded,
// cookie-carrying, aborted-at-head probing with NubArca's real classification
// — 202 preparing / 404 unavailable / 206+video progressive / 200 HLS MIME.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  classifyVideoProbe,
  probeVideoSource,
  resolveExpoVideoSource,
  type VideoProbeFetch,
  type VideoProbeOutcome,
} from './videoProbe.ts';

const SRC = {
  uri: 'https://unit.test/api/files/own-1/video',
  headers: { cookie: 'NubArca.Auth=tok' },
};

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
});

// ── classify unit corners ────────────────────────────────────────────────────

test('classify corners', () => {
  assert.deepEqual(classifyVideoProbe(202, null), { phase: 'preparing' });
  assert.deepEqual(classifyVideoProbe(404, null), { phase: 'unavailable' });
  assert.deepEqual(classifyVideoProbe(403, 'text/html'), { phase: 'unavailable' });
  // A 206 whose content-type is NOT video is not playable through this route.
  assert.deepEqual(classifyVideoProbe(206, 'application/json'), {
    phase: 'unavailable',
  });
});

// API client contract tests: 401 classification, AbortSignal propagation,
// cookie forwarding and mutation method/path contracts. Uses a stubbed global
// fetch — no network, no RN runtime.

import assert from 'node:assert/strict';
import test from 'node:test';
import {
  ApiError,

  apiGet,
  apiPatch,
  apiPost,
  apiRequest,
  configureBaseUrl,
  DEFAULT_TIMEOUT_MS,
  setUnauthorizedHandler,
} from './client.ts';
import {
  setSessionCookieSource,
  type SessionCookieSource,
} from './sessionAccess.ts';
import { bulkAddAlbumItems, bulkRemoveAlbumItems, deleteAlbum } from './albums.ts';
import { listAlbumMedia } from './media.ts';
import { listImages } from './images.ts';
import { listVideos } from './videos.ts';

interface RecordedRequest {
  method: string;
  url: string;
  headers: Record<string, string>;
  body: string | undefined;
}

let lastRequest: RecordedRequest | null = null;
// Produces the response for the current request. Receives the fetch signal so
// a HUNG-server simulation can settle by rejection on abort — the way a real
// aborted fetch behaves.
let nextResponse: (signal?: AbortSignal) => Response | Promise<Response> = () =>
  new Response('{}', { status: 200 });

function okBody(body: unknown): void {
  nextResponse = () => new Response(JSON.stringify(body), { status: 200 });
}

// Simulates a server that never answers. Settles only via abort.
function hungServer(signal?: AbortSignal): Promise<Response> {
  return new Promise((_resolve, reject) => {
    const aborted = () => reject(new Error('This operation was aborted'));
    if (!signal) return new Promise<Response>(() => {});
    if (signal.aborted) {
      aborted();
      return;
    }
    signal.addEventListener('abort', aborted, { once: true });
  });
}

function installFetch(): void {
  // @ts-expect-error — replacing global fetch in the test harness.
  globalThis.fetch = async (
    url: string,
    init: {
      method?: string;
      headers?: Record<string, string>;
      body?: string;
      signal?: AbortSignal;
    },
  ) => {
    lastRequest = {
      method: init?.method ?? 'GET',
      url,
      headers: init?.headers ?? {},
      body: init?.body,
    };
    const signal = init?.signal;
    if (signal?.aborted) throw new Error('This operation was aborted');
    return await nextResponse(signal);
  };
}

const COOKIE = `NubArca.Auth=${'k'.repeat(30)}`;
const session: SessionCookieSource = { current: COOKIE, capture: () => {} };

test.before(() => {
  installFetch();
  configureBaseUrl('https://nubarca.example');
  setSessionCookieSource(session);
});

test('GET forwards the exact session cookie', async () => {
  okBody({ items: [] });
  await apiGet('/api/images');
  assert.equal(lastRequest!.headers.cookie, COOKIE);
  assert.equal(lastRequest!.url, 'https://nubarca.example/api/images');
});

test('401 on an authenticated request throws ApiError AND fires the global handler', async () => {
  let fired = 0;
  setUnauthorizedHandler(() => {
    fired += 1;
  });
  nextResponse = () => new Response('{"error":"unauthorized"}', { status: 401 });
  await assert.rejects(apiGet('/api/auth/me'), (err: unknown) => {
    assert.ok(err instanceof ApiError);
    assert.equal((err as ApiError).status, 401);
    // No cookie value or body content in the message.
    assert.doesNotMatch((err as Error).message, /k{10}|unauthorized/);
    return true;
  });
  assert.equal(fired, 1);
  setUnauthorizedHandler(null);
});

test('401 with allow401 does NOT fire the global handler (login case)', async () => {
  let fired = 0;
  setUnauthorizedHandler(() => {
    fired += 1;
  });
  nextResponse = () => new Response('{}', { status: 401 });
  await assert.rejects(
    apiPost('/api/auth/login', { email: 'a@b.c', password: 'x' }, { allow401: true }),
    (err: unknown) => err instanceof ApiError && (err as ApiError).status === 401,
  );
  assert.equal(fired, 0, 'wrong credentials are not a dead session');
  setUnauthorizedHandler(null);
});

test('AbortSignal propagation: aborting the caller aborts the request', async () => {
  const external = new AbortController();
  nextResponse = (signal) => hungServer(signal);
  const pending = apiGet('/api/images', external.signal);
  external.abort();
  await assert.rejects(pending, (err: unknown) => err instanceof Error);
});

test('timeout aborts a hung request', async () => {
  nextResponse = (signal) => hungServer(signal);
  await assert.rejects(
    apiRequest('GET', '/api/images', { timeoutMs: 20 }),
    (err: unknown) => err instanceof Error,
  );
  assert.ok(DEFAULT_TIMEOUT_MS > 0);
});

test('mutation method/path contracts', async () => {
  okBody({ requested: 2, succeeded: 2, skipped: 0 });
  await bulkAddAlbumItems('alb-1', ['f1', 'f2']);
  assert.equal(lastRequest!.method, 'POST');
  assert.equal(lastRequest!.url, 'https://nubarca.example/api/albums/alb-1/items/bulk');
  assert.deepEqual(JSON.parse(lastRequest!.body!), { fileItemIds: ['f1', 'f2'] });

  await bulkRemoveAlbumItems('alb-1', ['f1']);
  assert.equal(lastRequest!.method, 'DELETE');
  assert.equal(lastRequest!.url, 'https://nubarca.example/api/albums/alb-1/items/bulk');
  assert.deepEqual(JSON.parse(lastRequest!.body!), { fileItemIds: ['f1'] });

  await deleteAlbum('alb-1');
  assert.equal(lastRequest!.method, 'DELETE');
  assert.equal(lastRequest!.url, 'https://nubarca.example/api/albums/alb-1');
  assert.equal(lastRequest!.body, undefined);
});

test('album item removal is ALBUM MEMBERSHIP, never file deletion', async () => {
  // The only DELETE routes the albums module exposes are album-scoped. A file
  // DELETE would be /api/files/{id} — the albums module must never emit one.
  okBody({ requested: 1, succeeded: 1, skipped: 0 });
  await bulkRemoveAlbumItems('alb-9', ['file-7']);
  assert.equal(lastRequest!.url, 'https://nubarca.example/api/albums/alb-9/items/bulk');
  assert.notEqual(lastRequest!.url, 'https://nubarca.example/api/files/file-7');
});

test('gallery list endpoints hit the unified contracts', async () => {
  okBody({ items: [], hasMore: false, nextCursor: null });
  await listImages({ sort: 'datetaken', direction: 'desc' });
  assert.match(lastRequest!.url!, /\/api\/images\?sort=datetaken&direction=desc$/);
  await listVideos({ cursor: 'c2' });
  assert.match(lastRequest!.url!, /\/api\/videos\?cursor=c2$/);
  await listAlbumMedia('a1', { kind: 'all', limit: 60 });
  assert.match(lastRequest!.url!, /\/api\/albums\/a1\/media\?kind=all&limit=60$/);
  await apiPatch('/api/albums/a1', { name: 'N', description: null });
  assert.equal(lastRequest!.method, 'PATCH');
  assert.deepEqual(JSON.parse(lastRequest!.body!), { name: 'N', description: null });
});

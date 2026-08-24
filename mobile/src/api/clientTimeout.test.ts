// Client timeout test: every request must enforce its wall-clock budget even
// when the transport never answers — no operation may hang forever.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { apiRequest } from './client.ts';
import { setSessionCookieSource } from './sessionAccess.ts';

setSessionCookieSource({ current: null, capture: () => {} });

test('a hanging request is aborted after timeoutMs', async () => {
  const original = globalThis.fetch;
  globalThis.fetch = ((_url: string | URL, init?: { signal?: AbortSignal }) => {
    return new Promise((_, reject) => {
      init?.signal?.addEventListener('abort', () => {
        reject(new Error('Request timed out after 40ms'));
      });
    });
  }) as typeof fetch;

  try {
    await assert.rejects(
      apiRequest('GET', '/api/slow', { timeoutMs: 40 }),
      /timed out/i,
    );
  } finally {
    globalThis.fetch = original;
  }
});

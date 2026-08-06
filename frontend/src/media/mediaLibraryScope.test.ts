import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  listImages, listVideos, excludeFromMediaLibrary, restoreToMediaLibrary,
} from '@nubarca/api-client';

// Slice 3 wire contract: the media-library scope param + the bulk
// exclude/restore endpoints. Both galleries must emit `mediaScope` with the
// same vocabulary and omit it for the default (active) scope, so an active
// cursor's fingerprint is never perturbed.

interface Captured { url: string; method: string; body: string | null }

function capture(response: unknown = { items: [], limit: 50, offset: 0, count: 0, nextCursor: null, hasMore: false }) {
  const calls: Captured[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({
      url: typeof input === 'string' ? input : String(input),
      method: (init?.method ?? 'GET').toUpperCase(),
      body: typeof init?.body === 'string' ? init.body : null,
    });
    return new Response(JSON.stringify(response), { status: 200, headers: { 'content-type': 'application/json' } });
  });
  vi.stubGlobal('fetch', fetchMock);
  return calls;
}

afterEach(() => vi.unstubAllGlobals());

describe('mediaScope query param', () => {
  it('omits mediaScope for the default active scope (images + videos)', async () => {
    const calls = capture();
    await listImages({});
    await listImages({ mediaScope: 'active' });
    await listVideos({});
    await listVideos({ mediaScope: 'active' });
    for (const c of calls) expect(c.url).not.toContain('mediaScope');
  });

  it('sends mediaScope=excluded for the Esclusi tab (images + videos)', async () => {
    const calls = capture();
    await listImages({ mediaScope: 'excluded' });
    await listVideos({ mediaScope: 'excluded' });
    expect(calls[0].url).toContain('mediaScope=excluded');
    expect(calls[1].url).toContain('mediaScope=excluded');
  });
});

describe('exclude / restore endpoints', () => {
  it('POSTs the file ids to /api/media-library/exclude', async () => {
    const calls = capture({ requested: 2, changed: 2, unchanged: 0, notFoundOrNotOwned: 0 });
    const result = await excludeFromMediaLibrary(['a', 'b']);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toContain('/api/media-library/exclude');
    expect(JSON.parse(calls[0].body!)).toEqual({ fileIds: ['a', 'b'] });
    expect(result.changed).toBe(2);
  });

  it('POSTs the file ids to /api/media-library/restore', async () => {
    const calls = capture({ requested: 1, changed: 1, unchanged: 0, notFoundOrNotOwned: 0 });
    await restoreToMediaLibrary(['x']);
    expect(calls[0].method).toBe('POST');
    expect(calls[0].url).toContain('/api/media-library/restore');
    expect(JSON.parse(calls[0].body!)).toEqual({ fileIds: ['x'] });
  });
});

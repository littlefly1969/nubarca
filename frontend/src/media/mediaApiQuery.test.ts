import { afterEach, describe, expect, it, vi } from 'vitest';
import { listImages, listVideos } from '@nubarca/api-client';

// Wire-level contract for the shared `albumMembership` filter. Both galleries
// must emit the SAME parameter with the SAME vocabulary, and must omit it when
// no constraint is requested — otherwise the backend cursor fingerprint would
// change for a filter the user never applied.

function captureUrls() {
  const urls: string[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    urls.push(typeof input === 'string' ? input : String(input));
    return new Response(
      JSON.stringify({ items: [], limit: 50, offset: 0, count: 0, nextCursor: null, hasMore: false }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  });
  vi.stubGlobal('fetch', fetchMock);
  return urls;
}

afterEach(() => vi.unstubAllGlobals());

describe('listImages albumMembership', () => {
  it.each(['assigned', 'unassigned'] as const)('sends albumMembership=%s', async (value) => {
    const urls = captureUrls();
    await listImages({ albumMembership: value });
    expect(urls[0]).toContain(`albumMembership=${value}`);
  });

  it('omits the parameter for "any" and when unset', async () => {
    const urls = captureUrls();
    await listImages({ albumMembership: 'any' });
    await listImages({});
    expect(urls[0]).not.toContain('albumMembership');
    expect(urls[1]).not.toContain('albumMembership');
  });

  it('composes with the other gallery filters', async () => {
    const urls = captureUrls();
    await listImages({ albumMembership: 'unassigned', favorite: true, sort: 'name', direction: 'asc' });
    expect(urls[0]).toContain('albumMembership=unassigned');
    expect(urls[0]).toContain('favorite=true');
    expect(urls[0]).toContain('sort=name');
  });
});

describe('listVideos albumMembership', () => {
  it.each(['assigned', 'unassigned'] as const)('sends albumMembership=%s', async (value) => {
    const urls = captureUrls();
    await listVideos({ albumMembership: value });
    expect(urls[0]).toContain(`albumMembership=${value}`);
  });

  it('omits the parameter for "any" and when unset', async () => {
    const urls = captureUrls();
    await listVideos({ albumMembership: 'any' });
    await listVideos({});
    expect(urls[0]).not.toContain('albumMembership');
    expect(urls[1]).not.toContain('albumMembership');
  });

  it('uses the same parameter name and vocabulary as the photo gallery', async () => {
    const urls = captureUrls();
    await listImages({ albumMembership: 'assigned' });
    await listVideos({ albumMembership: 'assigned' });
    const paramOf = (url: string) => new URL(url, 'http://x').searchParams.get('albumMembership');
    expect(paramOf(urls[0])).toBe('assigned');
    expect(paramOf(urls[1])).toBe('assigned');
  });
});

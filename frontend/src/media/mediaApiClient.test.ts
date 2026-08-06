import { afterEach, describe, expect, it, vi } from 'vitest';
import { listAlbumMedia, listMedia, type ListMediaQuery } from '@nubarca/api-client';

// Wire-level contract for the unified /api/media + /api/albums/{id}/media
// client. `kind` is always sent; scope/active is omitted; photo and video params
// serialize under their own names so the backend can reject an incompatible mix.

function captureUrls() {
  const urls: string[] = [];
  const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
    urls.push(typeof input === 'string' ? input : String(input));
    return new Response(
      JSON.stringify({
        items: [], limit: 50, count: 0, nextCursor: null, hasMore: false,
        total: 0, photoCount: 0, videoCount: 0,
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    );
  });
  vi.stubGlobal('fetch', fetchMock);
  return urls;
}

afterEach(() => vi.unstubAllGlobals());

const params = (url: string) => new URL(url, 'http://x').searchParams;

describe('listMedia', () => {
  it('always sends kind and omits scope=active', async () => {
    const urls = captureUrls();
    await listMedia({ kind: 'all' });
    expect(urls[0]).toContain('/api/media?');
    expect(params(urls[0]).get('kind')).toBe('all');
    expect(params(urls[0]).has('scope')).toBe(false);
  });

  it('sends scope=excluded', async () => {
    const urls = captureUrls();
    await listMedia({ kind: 'image', scope: 'excluded' });
    expect(params(urls[0]).get('scope')).toBe('excluded');
  });

  it('serializes photo filters', async () => {
    const urls = captureUrls();
    const q: ListMediaQuery = {
      kind: 'image', hasGps: true, collapseDuplicates: true,
      includePeople: ['p1', 'p2'], includePeopleMode: 'any', similarTo: 's1',
    };
    await listMedia(q);
    const p = params(urls[0]);
    expect(p.get('hasGps')).toBe('true');
    expect(p.get('collapseDuplicates')).toBe('true');
    expect(p.get('includePeople')).toBe('p1,p2');
    expect(p.get('includePeopleMode')).toBe('any');
    expect(p.get('similarTo')).toBe('s1');
  });

  it('serializes video filters', async () => {
    const urls = captureUrls();
    await listMedia({ kind: 'video', durationMin: 5, minHeight: 1080, codec: 'hevc', hasAudio: false });
    const p = params(urls[0]);
    expect(p.get('durationMin')).toBe('5');
    expect(p.get('minHeight')).toBe('1080');
    expect(p.get('codec')).toBe('hevc');
    expect(p.get('hasAudio')).toBe('false');
  });

  it('omits albumMembership=any', async () => {
    const urls = captureUrls();
    await listMedia({ kind: 'all', albumMembership: 'any' });
    expect(params(urls[0]).has('albumMembership')).toBe(false);
  });
});

describe('listAlbumMedia', () => {
  it('targets the album media route', async () => {
    const urls = captureUrls();
    await listAlbumMedia('album-1', { kind: 'video' });
    expect(urls[0]).toContain('/api/albums/album-1/media?');
    expect(params(urls[0]).get('kind')).toBe('video');
  });
});

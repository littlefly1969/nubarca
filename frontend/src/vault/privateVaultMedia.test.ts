import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  fetchVaultPoster,
  fetchVaultPreview,
  fetchVaultThumbnail,
  getVaultMediaInfo,
} from '@nubarca/api-client';

// api-client vault media byte helpers (slice 4). The token must travel ONLY in
// the X-Vault-Token header — never the URL — and the helpers must validate the
// response (401 distinctly, content-type, size) before handing back a Blob.

const TOKEN = 'vault-secret-token';

function imageResponse(bytes = new Uint8Array([1, 2, 3]), contentLength?: number): Response {
  const headers: Record<string, string> = { 'content-type': 'image/jpeg' };
  if (contentLength !== undefined) headers['content-length'] = String(contentLength);
  return new Response(bytes, { status: 200, headers });
}

let fetchMock: ReturnType<typeof vi.fn>;

beforeEach(() => {
  fetchMock = vi.fn(async () => imageResponse());
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('fetchVaultThumbnail', () => {
  it('sends the token only in the X-Vault-Token header, never the URL', async () => {
    await fetchVaultThumbnail(TOKEN, 'file-1', 'small');
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('/api/private-vault/media/file-1/thumbnail?size=small');
    expect(url).not.toContain(TOKEN);
    const headers = init.headers as Record<string, string>;
    expect(headers['X-Vault-Token']).toBe(TOKEN);
    expect(init.credentials).toBe('include');
  });

  it('returns image bytes as a Blob for an image response', async () => {
    const blob = await fetchVaultThumbnail(TOKEN, 'file-1', 'medium');
    // jsdom/undici can hand back a Blob from a different realm, so assert on
    // shape rather than instanceof.
    expect(blob.size).toBe(3);
    expect(blob.type).toBe('image/jpeg');
  });

  it('maps 401 to an ApiError with status 401', async () => {
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }));
    await expect(fetchVaultThumbnail(TOKEN, 'file-1', 'small')).rejects.toMatchObject({
      name: 'ApiError',
      status: 401,
    });
  });

  it('rejects a non-image content type', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response('<html></html>', { status: 200, headers: { 'content-type': 'text/html' } }),
    );
    await expect(fetchVaultThumbnail(TOKEN, 'file-1', 'small')).rejects.toBeInstanceOf(ApiError);
  });

  it('rejects an over-large declared content length', async () => {
    fetchMock.mockResolvedValueOnce(imageResponse(new Uint8Array([1]), 60 * 1024 * 1024));
    await expect(fetchVaultThumbnail(TOKEN, 'file-1', 'small')).rejects.toBeInstanceOf(ApiError);
  });
});

describe('fetchVaultPreview / fetchVaultPoster', () => {
  it('hit the preview and poster endpoints with the header', async () => {
    await fetchVaultPreview(TOKEN, 'p1');
    await fetchVaultPoster(TOKEN, 'v1');
    const previewUrl = fetchMock.mock.calls[0][0] as string;
    const posterUrl = fetchMock.mock.calls[1][0] as string;
    expect(previewUrl).toContain('/api/private-vault/media/p1/preview');
    expect(posterUrl).toContain('/api/private-vault/media/v1/poster');
    for (const call of fetchMock.mock.calls) {
      const init = call[1] as RequestInit;
      expect((init.headers as Record<string, string>)['X-Vault-Token']).toBe(TOKEN);
    }
  });
});

describe('getVaultMediaInfo', () => {
  it('requests the info endpoint with the vault header', async () => {
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ id: 'p1', name: 'a.png', displayName: 'a.png' }), {
        status: 200,
        headers: { 'content-type': 'application/json' },
      }),
    );
    const info = await getVaultMediaInfo(TOKEN, 'p1');
    expect(info.id).toBe('p1');
    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toContain('/api/private-vault/media/p1/info');
    expect(url).not.toContain(TOKEN);
    expect((init.headers as Record<string, string>)['X-Vault-Token']).toBe(TOKEN);
  });
});

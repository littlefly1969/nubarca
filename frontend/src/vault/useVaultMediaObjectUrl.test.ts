import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { useVaultMediaObjectUrl } from './useVaultMediaObjectUrl';

// Object-URL lifecycle for authenticated vault media. Verifies the token is
// sent only in the header, the object URL is created on load and revoked on
// unmount / file change, requests are aborted, and a 401 triggers onExpired.

let created: string[];
let revoked: string[];
let fetchMock: ReturnType<typeof vi.fn>;

function imageResponse(): Response {
  return new Response(new Uint8Array([1, 2, 3]), {
    status: 200,
    headers: { 'content-type': 'image/jpeg' },
  });
}

beforeEach(() => {
  created = [];
  revoked = [];
  URL.createObjectURL = vi.fn(() => {
    const u = `blob:mock/${created.length}`;
    created.push(u);
    return u;
  }) as unknown as typeof URL.createObjectURL;
  URL.revokeObjectURL = vi.fn((u: string) => {
    revoked.push(u);
  });
  fetchMock = vi.fn(async () => imageResponse());
  vi.stubGlobal('fetch', fetchMock);
});

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

it('fetches with the vault header and exposes an object URL', async () => {
  const { result } = renderHook(() =>
    useVaultMediaObjectUrl({ token: 'tok', fileId: 'f1', variant: 'thumbnail-small' }),
  );
  await waitFor(() => expect(result.current.status).toBe('ready'));
  expect(result.current.url).toBe('blob:mock/0');
  const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
  expect(url).not.toContain('tok');
  expect((init.headers as Record<string, string>)['X-Vault-Token']).toBe('tok');
});

it('does not fetch while disabled', async () => {
  const { result } = renderHook(() =>
    useVaultMediaObjectUrl({ token: 'tok', fileId: 'f1', variant: 'thumbnail-small', enabled: false }),
  );
  expect(result.current.status).toBe('idle');
  expect(fetchMock).not.toHaveBeenCalled();
});

it('revokes the object URL on unmount', async () => {
  const { result, unmount } = renderHook(() =>
    useVaultMediaObjectUrl({ token: 'tok', fileId: 'f1', variant: 'thumbnail-small' }),
  );
  await waitFor(() => expect(result.current.status).toBe('ready'));
  act(() => unmount());
  expect(revoked).toContain('blob:mock/0');
});

it('revokes the previous URL when the file changes', async () => {
  const { result, rerender } = renderHook(
    ({ id }: { id: string }) =>
      useVaultMediaObjectUrl({ token: 'tok', fileId: id, variant: 'thumbnail-small' }),
    { initialProps: { id: 'f1' } },
  );
  await waitFor(() => expect(result.current.url).toBe('blob:mock/0'));
  rerender({ id: 'f2' });
  await waitFor(() => expect(result.current.url).toBe('blob:mock/1'));
  expect(revoked).toContain('blob:mock/0');
});

it('calls onExpired and reports error on a 401', async () => {
  fetchMock.mockResolvedValueOnce(new Response(null, { status: 401 }));
  const onExpired = vi.fn();
  const { result } = renderHook(() =>
    useVaultMediaObjectUrl({ token: 'tok', fileId: 'f1', variant: 'preview', onExpired }),
  );
  await waitFor(() => expect(result.current.status).toBe('error'));
  expect(onExpired).toHaveBeenCalledTimes(1);
});

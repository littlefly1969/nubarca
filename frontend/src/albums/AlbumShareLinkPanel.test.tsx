import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { AlbumShareLinkPanel } from './AlbumShareLinkPanel';

// THE PRINT SWITCH on an album's link: offered only where it can mean
// anything — an email + second-factor link — and saved on its own.

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const ALBUM = 'A1';

function link(over: Record<string, unknown> = {}) {
  return {
    id: 'l1', url: '/album/tok', enabled: true, uploadEnabled: true, allowOriginalDownload: false,
    requireSecondFactor: false, label: null, maxUploads: 2000, uploadCount: 0,
    createdAt: '2026-01-01T00:00:00Z', expiresAt: null, guests: [], allowPrint: false, ...over,
  };
}

function mount(current: ReturnType<typeof link>) {
  const mock = installFetchMock({
    [`GET /api/albums/${ALBUM}/share-link`]: () => jsonResponse(current),
    [`PATCH /api/albums/${ALBUM}/share-link`]: () => jsonResponse({ ...current, allowPrint: true }),
  });
  render(<AuthedWrapper><AlbumShareLinkPanel albumId={ALBUM} /></AuthedWrapper>);
  return mock;
}

describe('AlbumShareLinkPanel print switch', () => {
  it('is not offered on a public link', async () => {
    mount(link());
    expect(await screen.findByTestId('album-share-link-second-factor')).toBeInTheDocument();
    expect(screen.queryByTestId('album-share-link-print')).toBeNull();
  });

  it('is offered on an email + second-factor link, off by default, and saves only itself', async () => {
    const user = userEvent.setup();
    const mock = mount(link({ requireSecondFactor: true }));
    const toggle = await screen.findByTestId('album-share-link-print');
    expect(toggle).toHaveAccessibleName('Consenti stampa');
    expect(toggle).toHaveAttribute('aria-checked', 'false');

    await user.click(toggle);
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'PATCH')).toBe(true));
    const patch = mock.calls.find((c) => c.method === 'PATCH')!;
    expect(JSON.parse(patch.body ?? '{}')).toEqual({ allowPrint: true });
    await waitFor(() => expect(screen.getByTestId('album-share-link-print')).toHaveAttribute('aria-checked', 'true'));
  });
});

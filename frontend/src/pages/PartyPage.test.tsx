import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyPage } from './PartyPage';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

function wrapper(token = 'tok-1') {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${token}`]}>
        <Routes>
          <Route path="/party/:token" element={<PartyPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

const items = {
  albumName: 'Beach Party',
  items: [
    {
      id: 'f1', mediaType: 'image',
      thumbnailUrl: '/api/party/tok-1/media/f1/thumbnail',
      previewUrl: '/api/party/tok-1/media/f1/preview',
      downloadUrl: '/api/party/tok-1/media/f1/download',
    },
  ],
};

describe('PartyPage (public party landing)', () => {
  it('renders the album and opens a photo with a download link', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: 1 }),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });

    render(wrapper());
    expect(await screen.findByRole('heading', { name: 'Beach Party' })).toBeInTheDocument();

    const grid = await screen.findByTestId('party-grid');
    expect(grid).toBeInTheDocument();

    // Open the lightbox → the medium PREVIEW is shown (never the original) and a
    // metadata-stripped download is offered.
    await userEvent.setup().click(screen.getByRole('button', { name: /Apri foto/i }));
    const dialog = await screen.findByRole('dialog', { name: /Visualizzatore foto/i });
    expect(dialog.querySelector('img')).toHaveAttribute('src', '/api/party/tok-1/media/f1/preview');
    const download = screen.getByRole('link', { name: /Scarica/i });
    expect(download).toHaveAttribute('href', '/api/party/tok-1/media/f1/download');

    // No upload UI on the public page.
    expect(screen.queryByText(/upload/i)).not.toBeInTheDocument();
    expect(document.querySelector('input[type="file"]')).toBeNull();
  });

  it('shows an unavailable message for a revoked/expired token (404)', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => errorResponse(404),
      'GET /api/party/tok-1/items': () => errorResponse(404),
    });

    render(wrapper());
    expect(await screen.findByText(/non è più disponibile/i)).toBeInTheDocument();
  });

  it('does not expose owner/metadata/upload surfaces', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: 1 }),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });

    render(wrapper());
    await screen.findByTestId('party-grid');
    // No face/person, GPS, or upload controls leak into the public UI.
    expect(screen.queryByText(/upload/i)).not.toBeInTheDocument();
    expect(screen.queryByText(/gps|location|face|person/i)).not.toBeInTheDocument();
  });

  const two = {
    albumName: 'Beach Party',
    items: [
      items.items[0],
      {
        id: 'f2', mediaType: 'image',
        thumbnailUrl: '/api/party/tok-1/media/f2/thumbnail',
        previewUrl: '/api/party/tok-1/media/f2/preview',
        downloadUrl: '/api/party/tok-1/media/f2/download',
      },
    ],
  };

  // Flush mount/poll promise cascades under fake timers.
  async function settle() {
    for (let i = 0; i < 6; i += 1) {
      // eslint-disable-next-line no-await-in-loop
      await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    }
  }

  it('live-refreshes to show a newly uploaded photo', async () => {
    let current = items;
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: current.items.length }),
      'GET /api/party/tok-1/items': () => jsonResponse(current),
    });
    vi.useFakeTimers();
    try {
      render(wrapper());
      await settle();
      expect(screen.getAllByRole('button', { name: /Apri foto/i })).toHaveLength(1);

      // A guest uploads another photo → the next poll appends it.
      current = two;
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getAllByRole('button', { name: /Apri foto/i })).toHaveLength(2);
    } finally {
      vi.useRealTimers();
    }
  });

  it('drops a hidden item and closes its lightbox on the next refresh', async () => {
    let current = two;
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: current.items.length }),
      'GET /api/party/tok-1/items': () => jsonResponse(current),
    });
    vi.useFakeTimers();
    try {
      render(wrapper());
      await settle();
      expect(screen.getAllByRole('button', { name: /Apri foto/i })).toHaveLength(2);

      // Open the second photo's lightbox.
      const openButtons = screen.getAllByRole('button', { name: /Apri foto/i });
      await act(async () => { openButtons[1].click(); });
      await settle();
      expect(screen.getByRole('dialog', { name: /Visualizzatore foto/i })).toBeInTheDocument();

      // Owner hides f2 → the next poll returns only f1: the grid drops it AND the
      // open lightbox (which was showing f2) closes.
      current = items;
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getAllByRole('button', { name: /Apri foto/i })).toHaveLength(1);
      expect(screen.queryByRole('dialog', { name: /Visualizzatore foto/i })).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('becomes unavailable when the token is revoked during refresh', async () => {
    let revoked = false;
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: 1 }),
      'GET /api/party/tok-1/items': () => (revoked ? errorResponse(404) : jsonResponse(items)),
    });
    vi.useFakeTimers();
    try {
      render(wrapper());
      await settle();
      expect(screen.getByTestId('party-grid')).toBeInTheDocument();

      revoked = true;
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getByText(/non è più disponibile/i)).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('a completed face search filters ONLY the page grid; cancel restores the full album; the TV is never called', async () => {
    const twoItems = {
      albumName: 'Beach Party',
      items: [
        items.items[0],
        {
          id: 'f2', mediaType: 'image',
          thumbnailUrl: '/api/party/tok-1/media/f2/thumbnail',
          previewUrl: '/api/party/tok-1/media/f2/preview',
          downloadUrl: '/api/party/tok-1/media/f2/download',
        },
      ],
    };
    const mock = installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ albumName: 'Beach Party', itemCount: 2 }),
      'GET /api/party/tok-1/items': () => jsonResponse(twoItems),
      'POST /api/party/tok-1/face-search': () => jsonResponse({
        status: 'ready', searchId: 's1', resultCount: 1, items: [twoItems.items[1]],
      }),
      'DELETE /api/party/tok-1/face-search/s1': () => jsonResponse(null, 204),
    });

    render(wrapper());
    const user = userEvent.setup();
    await screen.findByTestId('party-grid');
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-tile')).toHaveLength(2);

    // Run a face search: only the matching photo stays visible; the full album
    // remains in state (the item count subtitle is unchanged).
    await user.click(screen.getByTestId('party-face-open'));
    await user.upload(
      screen.getByTestId('party-face-input'),
      new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' }),
    );
    await user.click(screen.getByTestId('party-face-submit'));
    await screen.findByTestId('party-face-count');
    const tiles = screen.getByTestId('party-grid').querySelectorAll('button.party-tile img');
    expect(tiles).toHaveLength(1);
    expect(tiles[0]).toHaveAttribute('src', '/api/party/tok-1/media/f2/thumbnail');

    // A matching photo can still be opened + downloaded.
    await user.click(screen.getByTestId('party-grid').querySelector('button.party-tile')!);
    const dialog = await screen.findByRole('dialog');
    expect(screen.getByRole('link', { name: /Scarica/i }))
      .toHaveAttribute('href', '/api/party/tok-1/media/f2/download');
    const { within } = await import('@testing-library/react');
    await user.click(within(dialog).getByRole('button', { name: /Chiudi/i }));

    // No TV endpoint was ever touched by completing the search.
    expect(mock.calls.some((c) => c.url.includes('/api/tv/') || c.url.includes('activate-tv'))).toBe(false);

    // Cancel search → server-side delete + full album restored.
    await user.click(screen.getByTestId('party-face-cancel'));
    await screen.findByTestId('party-grid');
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-tile')).toHaveLength(2);
    expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/face-search/s1'))).toBe(true);
  });

  it('the party grid CSS keeps a definite width so auto-fill columns derive from the viewport (mobile overflow regression)', async () => {
    // jsdom performs no layout, so this guards the root-cause fix statically:
    // .party-grid must declare width:100% (+ min-width:0). Without a definite
    // width, the grid inside the column-flex face panel resolved auto-fill
    // against max-width (68rem → 6 columns ≈ 940px) and overflowed phones.
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const css = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8');
    const rule = css.match(/\.party-grid\s*\{([^}]*)\}/)?.[1] ?? '';
    expect(rule).toContain('width: 100%');
    expect(rule).toContain('min-width: 0');
    expect(rule).toContain('grid-template-columns: repeat(auto-fill, minmax(150px, 1fr))');
  });
});

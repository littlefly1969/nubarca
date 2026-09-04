import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
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
  const hub = {
    albumName: 'Beach Party', itemCount: 1,
    coverUrl: '/api/party/tok-1/media/f1/preview',
    contributionUrl: '/party/upload-token/upload', gameEnabled: true,
  };

  it('is the canonical Guest Hub and keeps both legacy capabilities reachable', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse(hub),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
    render(wrapper());
    // Contributing is now the hero's primary call to action; it still points at
    // the contribution URL the backend returned, and nothing else duplicates it.
    expect(await screen.findByRole('link', { name: /Condividi un momento/i }))
      .toHaveAttribute('href', '/party/upload-token/upload');
    expect(screen.getAllByRole('link', { name: /Condividi un momento/i })).toHaveLength(1);
    expect(screen.getByRole('link', { name: /Sfide e votazioni/i }))
      .toHaveAttribute('href', '/party/tok-1/challenges');
    expect(screen.getByTestId('party-hub-cover')).toHaveStyle({
      backgroundImage: 'url("/api/party/tok-1/media/f1/preview")',
    });
    expect(screen.getByTestId('party-capability-face')).toBeInTheDocument();
    expect(screen.getByTestId('party-grid')).toBeInTheDocument();
  });

  // --- Guest-hub first viewport -------------------------------------------

  it('shows the OFFICIAL NubArca wordmark, never a text or restyled logo', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse(hub),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
    render(wrapper());
    const logo = await screen.findByRole('img', { name: 'NubArca' });
    // A byte-exact approved on-dark asset, at its own proportions: the guest hub
    // is a fixed dark surface, so it never resolves the on-light artwork.
    expect(logo).toHaveAttribute('src', '/brand/nubarca-wordmark-on-dark-480w.png');
    expect(logo).toHaveAttribute('width', '480');
    expect(logo).toHaveAttribute('height', '135');
    expect(logo.getAttribute('style')).toBeNull();
  });

  it('makes the cover the hero and marks it decorative', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse(hub),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
    render(wrapper());
    const cover = await screen.findByTestId('party-hub-cover');
    expect(cover).toHaveStyle({ backgroundImage: 'url("/api/party/tok-1/media/f1/preview")' });
    expect(cover).toHaveAttribute('data-cover', 'photo');
    // The album name is the heading; the cover art itself carries no semantics.
    expect(cover).toHaveAttribute('aria-hidden', 'true');
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Beach Party');
    // Live state: the dynamic item count next to a "Live" marker. Scoped to the
    // hero — the challenges card carries its own LIVE badge.
    const hero = cover.parentElement as HTMLElement;
    expect(within(hero).getByText(/1 elemento/)).toBeInTheDocument();
    expect(within(hero).getByText('Live')).toBeInTheDocument();
  });

  it('falls back to the branded NubArca cover when the album has none', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ ...hub, coverUrl: null }),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
    render(wrapper());
    const cover = await screen.findByTestId('party-hub-cover');
    // No inline background image: the fallback composition is the stylesheet's,
    // selected by the data attribute.
    expect(cover).toHaveAttribute('data-cover', 'fallback');
    expect(cover.style.backgroundImage).toBe('');
    // The hero is still branded and still leads to the same action.
    expect(screen.getByRole('img', { name: 'NubArca' })).toBeInTheDocument();
    expect(screen.getByTestId('party-hub-cta')).toHaveAttribute('href', '/party/upload-token/upload');
  });

  it('shows NO contribution CTA when the backend returns no contribution URL', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ ...hub, contributionUrl: null }),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
    render(wrapper());
    await screen.findByTestId('party-grid');
    // Never a dead or href-less action: the rest of the hub keeps working.
    expect(screen.queryByTestId('party-hub-cta')).not.toBeInTheDocument();
    expect(screen.queryByRole('link', { name: /Condividi un momento/i })).not.toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Beach Party');
  });

  it('keeps the loading skeleton on the branded shell and shaped like the hero', async () => {
    installFetchMock({
      'GET /api/party/tok-1': () => new Promise<Response>(() => {}),
      'GET /api/party/tok-1/items': () => new Promise<Response>(() => {}),
    });
    render(wrapper());
    expect(document.querySelector('main.party-guest-hub')).toHaveAttribute('aria-busy', 'true');
    const skeleton = screen.getByTestId('party-hub-skeleton');
    // Hero-shaped: brand bar, title lines and the CTA are all reserved.
    expect(skeleton.querySelector('.party-guest-hub-shape-logo')).toBeInTheDocument();
    expect(skeleton.querySelector('.party-guest-hub-shape-title')).toBeInTheDocument();
    expect(skeleton.querySelector('.party-guest-hub-shape-cta')).toBeInTheDocument();
  });

  it('keeps the error state branded and retryable', async () => {
    let failing = true;
    installFetchMock({
      'GET /api/party/tok-1': () => (failing
        ? errorResponse(500)
        : jsonResponse(hub)),
      'GET /api/party/tok-1/items': () => (failing
        ? errorResponse(500)
        : jsonResponse(items)),
    });

    render(wrapper());
    expect(await screen.findByText(/Impossibile caricare questo album party/i)).toBeInTheDocument();
    // Not a bare technical page: the official wordmark is part of the state.
    expect(screen.getByRole('img', { name: 'NubArca' }))
      .toHaveAttribute('src', '/brand/nubarca-wordmark-on-dark-480w.png');

    // Retry re-runs the same load and recovers into the hub.
    failing = false;
    await userEvent.setup().click(screen.getByRole('button', { name: /Riprova/i }));
    expect(await screen.findByTestId('party-grid')).toBeInTheDocument();
    expect(screen.getByRole('heading', { level: 1 })).toHaveTextContent('Beach Party');
  });

  // --- Capability deck -----------------------------------------------------

  function mockHub(overrides: Record<string, unknown> = {}) {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ ...hub, ...overrides }),
      'GET /api/party/tok-1/items': () => jsonResponse(items),
    });
  }

  it('offers the real capabilities under one "what would you like to do?" heading', async () => {
    mockHub();
    render(wrapper());
    const deck = await screen.findByRole('navigation', { name: /Cosa vuoi fare\?/i });
    expect(screen.getByRole('heading', { level: 2, name: /Cosa vuoi fare\?/i })).toBeInTheDocument();

    // A real destination is a real link; an action that opens something in
    // place is a real button. Never a clickable div either way.
    expect(within(deck).getAllByRole('link')).toHaveLength(2);
    expect(within(deck).getByRole('link', { name: /Esplora l’album/i }))
      .toHaveAttribute('href', '#party-photos');
    expect(within(deck).getByRole('link', { name: /Sfide e votazioni/i }))
      .toHaveAttribute('href', '/party/tok-1/challenges');

    const face = within(deck).getByRole('button', { name: /Trova le tue foto/i });
    expect(face.tagName).toBe('BUTTON');
    // The anchor it used to be — and the second launcher it led to — are gone.
    expect(document.querySelector('a[href="#party-face"]')).toBeNull();
    expect(screen.queryByTestId('party-face-open')).not.toBeInTheDocument();
  });

  it('drops the challenges capability when the party game is off', async () => {
    mockHub({ gameEnabled: false });
    render(wrapper());
    const deck = await screen.findByRole('navigation', { name: /Cosa vuoi fare\?/i });
    expect(within(deck).queryByRole('link', { name: /Sfide e votazioni/i })).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-capability-challenges')).not.toBeInTheDocument();
    // The rest of the deck is unaffected: the album link plus the face action.
    expect(within(deck).getAllByRole('link')).toHaveLength(1);
    expect(within(deck).getByRole('button', { name: /Trova le tue foto/i })).toBeInTheDocument();
    expect(screen.getByTestId('party-capability-face')).toBeInTheDocument();
    expect(screen.getByTestId('party-capability-album')).toBeInTheDocument();
  });

  it('never duplicates the hero CTA in the deck', async () => {
    mockHub();
    render(wrapper());
    await screen.findByTestId('party-grid');
    // Exactly one link to the contribution URL on the whole page, and it is the
    // hero's, not a deck card.
    const contributing = Array.from(
      document.querySelectorAll('a[href="/party/upload-token/upload"]'),
    );
    expect(contributing).toHaveLength(1);
    expect(contributing[0]).toHaveAttribute('data-testid', 'party-hub-cta');
    const deck = screen.getByRole('navigation', { name: /Cosa vuoi fare\?/i });
    expect(within(deck).queryByRole('link', { name: /Condividi un momento/i })).not.toBeInTheDocument();
  });

  it('shows NO capability the product does not offer yet', async () => {
    mockHub();
    render(wrapper());
    const deck = await screen.findByRole('navigation', { name: /Cosa vuoi fare\?/i });
    // Dedication, song requests and printing are planned, not built: they must
    // not appear at all — not even as a disabled "coming soon" card.
    expect(deck.textContent).not.toMatch(/dedica|canzone|brano|stampa|ricordo/i);
    expect(deck.querySelectorAll('[disabled], [aria-disabled="true"]')).toHaveLength(0);
  });

  it('opens the face-search experience with ONE tap on its capability card', async () => {
    mockHub();
    render(wrapper());
    const user = userEvent.setup();
    await screen.findByTestId('party-capability-face');
    const card = screen.getByRole('button', { name: /Trova le tue foto/i });
    expect(screen.queryByRole('dialog')).not.toBeInTheDocument();

    // One tap — no scroll, no second launcher.
    await user.click(card);
    const sheet = await screen.findByTestId('party-face');
    expect(sheet).toHaveAttribute('aria-modal', 'true');
    expect(within(sheet).getByRole('heading', { name: 'Trova le tue foto' })).toBeInTheDocument();
    expect(screen.getByTestId('party-face-input')).toBeInTheDocument();

    // Escape closes it and hands focus back to the card that opened it.
    await user.keyboard('{Escape}');
    await waitFor(() => expect(screen.queryByTestId('party-face')).not.toBeInTheDocument());
    expect(document.activeElement).toBe(card);
  });

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
    // Still the guest experience, not a technical error page.
    expect(screen.getByRole('img', { name: 'NubArca' }))
      .toHaveAttribute('src', '/brand/nubarca-wordmark-on-dark-480w.png');
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
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile')).toHaveLength(2);

    // Run a face search: only the matching photo stays visible; the full album
    // remains in state (the item count subtitle is unchanged).
    await user.click(screen.getByRole('button', { name: /Trova le tue foto/i }));
    await user.upload(
      screen.getByTestId('party-face-input'),
      new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' }),
    );
    await user.click(screen.getByTestId('party-face-submit'));
    await screen.findByTestId('party-face-count');
    const tiles = screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile img');
    expect(tiles).toHaveLength(1);
    expect(tiles[0]).toHaveAttribute('src', '/api/party/tok-1/media/f2/thumbnail');

    // "See my photos" closes the sheet and leaves the filter applied, with the
    // page saying so — a filtered album is never an unexplained one.
    await user.click(screen.getByTestId('party-face-show-results'));
    await waitFor(() => expect(screen.queryByTestId('party-face')).not.toBeInTheDocument());
    expect(screen.getByTestId('party-face-filter')).toBeInTheDocument();
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile')).toHaveLength(1);

    // A matching photo can still be opened + downloaded.
    await user.click(screen.getByTestId('party-grid').querySelector('button.party-guest-hub-tile')!);
    const dialog = await screen.findByRole('dialog', { name: /Visualizzatore foto/i });
    expect(screen.getByRole('link', { name: /Scarica/i }))
      .toHaveAttribute('href', '/api/party/tok-1/media/f2/download');
    await user.click(within(dialog).getByRole('button', { name: /Chiudi/i }));

    // No TV endpoint was ever touched by completing the search.
    expect(mock.calls.some((c) => c.url.includes('/api/tv/') || c.url.includes('activate-tv'))).toBe(false);

    // Clearing it from the banner → server-side delete + full album restored.
    await user.click(screen.getByTestId('party-face-filter-clear'));
    await screen.findByTestId('party-grid');
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile')).toHaveLength(2);
    expect(screen.queryByTestId('party-face-filter')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/face-search/s1'))).toBe(true);
  });

  // --- Gallery -------------------------------------------------------------

  function media(id: string, mediaType: 'image' | 'video' = 'image') {
    return {
      id, mediaType,
      thumbnailUrl: `/api/party/tok-1/media/${id}/thumbnail`,
      previewUrl: `/api/party/tok-1/media/${id}/preview`,
      // The party surface serves videos as a poster, with no download.
      downloadUrl: mediaType === 'video' ? null : `/api/party/tok-1/media/${id}/download`,
    };
  }

  function mockAlbum(list: ReturnType<typeof media>[], album: Record<string, unknown> = {}) {
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ ...hub, ...album, itemCount: list.length }),
      'GET /api/party/tok-1/items': () => jsonResponse({ albumName: 'Beach Party', items: list }),
    });
  }

  it('presents the album as "moments from the party", live and counted', async () => {
    mockAlbum([media('f1'), media('f2'), media('f3')]);
    render(wrapper());
    const gallery = await screen.findByRole('region', { name: /Momenti della festa/i });
    expect(within(gallery).getByRole('heading', { level: 2, name: 'Momenti della festa' }))
      .toBeInTheDocument();
    expect(within(gallery).getByText('Si aggiorna mentre la festa continua')).toBeInTheDocument();
    expect(screen.getByTestId('party-gallery-count')).toHaveTextContent('3 elementi');
  });

  it('lays the tiles out in server order, with a deterministic editorial shape', async () => {
    mockAlbum(['f1', 'f2', 'f3', 'f4', 'f5'].map((id) => media(id)));
    render(wrapper());
    await screen.findByTestId('party-grid');
    const tiles = Array.from(
      screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile img'),
    );
    // DOM order IS the server's order — nothing reorders for looks.
    expect(tiles.map((t) => t.getAttribute('src'))).toEqual([
      '/api/party/tok-1/media/f1/thumbnail',
      '/api/party/tok-1/media/f2/thumbnail',
      '/api/party/tok-1/media/f3/thumbnail',
      '/api/party/tok-1/media/f4/thumbnail',
      '/api/party/tok-1/media/f5/thumbnail',
    ]);
    // The shapes come from the index, never from the images.
    const shapes = Array.from(
      screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile'),
    ).map((el) => el.getAttribute('data-shape'));
    expect(shapes).toEqual(['featured', 'square', 'square', 'portrait', 'portrait']);
  });

  it('composes a whole grid for a one-photo and a two-photo album', async () => {
    mockAlbum([media('f1')]);
    const one = render(wrapper());
    await screen.findByTestId('party-grid');
    expect(Array.from(document.querySelectorAll('button.party-guest-hub-tile'))
      .map((el) => el.getAttribute('data-shape'))).toEqual(['featured']);
    one.unmount();

    // Two photos: a lead tile plus one lonely half-row would look broken, so
    // both take the full width instead.
    mockAlbum([media('f1'), media('f2')]);
    render(wrapper());
    await screen.findByTestId('party-grid');
    expect(Array.from(document.querySelectorAll('button.party-guest-hub-tile'))
      .map((el) => el.getAttribute('data-shape'))).toEqual(['featured', 'featured']);
  });

  it('marks a video with a play indicator and invents no duration', async () => {
    mockAlbum([media('f1'), media('f2', 'video')]);
    render(wrapper());
    await screen.findByTestId('party-grid');
    const video = screen.getByRole('button', { name: 'Apri video' });
    expect(video.querySelector('.party-guest-hub-tile-play')).toBeInTheDocument();
    expect(video.querySelector('img')).toHaveAttribute('src', '/api/party/tok-1/media/f2/thumbnail');
    // No made-up runtime: the party API returns none.
    expect(video.textContent).not.toMatch(/\d+:\d\d/);
    // And no player is embedded in the grid.
    expect(document.querySelector('video')).toBeNull();
  });

  it('opens a video as the party-safe poster, with no download offered', async () => {
    mockAlbum([media('f1', 'video')]);
    render(wrapper());
    await userEvent.setup().click(await screen.findByRole('button', { name: 'Apri video' }));
    const viewer = await screen.findByRole('dialog', { name: /Visualizzatore foto/i });
    // previewUrl for a video IS the poster; the party surface has no playback
    // and no video download.
    expect(viewer.querySelector('img')).toHaveAttribute('src', '/api/party/tok-1/media/f1/preview');
    expect(viewer.querySelector('video')).toBeNull();
    expect(screen.queryByRole('link', { name: /Scarica/i })).not.toBeInTheDocument();
  });

  it('returns focus to the tile the viewer was opened from', async () => {
    mockAlbum([media('f1'), media('f2')]);
    render(wrapper());
    const user = userEvent.setup();
    await screen.findByTestId('party-grid');
    const tiles = screen.getAllByRole('button', { name: 'Apri foto' });

    await user.click(tiles[1]);
    await screen.findByRole('dialog', { name: /Visualizzatore foto/i });
    await user.click(screen.getByTestId('party-viewer-close'));
    await waitFor(() =>
      expect(screen.queryByRole('dialog', { name: /Visualizzatore foto/i })).not.toBeInTheDocument());
    expect(document.activeElement).toBe(tiles[1]);
  });

  it('counts MATCHES, not the album, while a face filter is applied', async () => {
    const twoItems = [media('f1'), media('f2'), media('f3')];
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ ...hub, itemCount: 3 }),
      'GET /api/party/tok-1/items': () => jsonResponse({ albumName: 'Beach Party', items: twoItems }),
      'POST /api/party/tok-1/face-search': () => jsonResponse({
        status: 'ready', searchId: 's1', resultCount: 1, items: [twoItems[1]],
      }),
      'DELETE /api/party/tok-1/face-search/s1': () => jsonResponse(null, 204),
    });
    render(wrapper());
    const user = userEvent.setup();
    await screen.findByTestId('party-grid');
    expect(screen.getByTestId('party-gallery-count')).toHaveTextContent('3 elementi');

    await user.click(screen.getByRole('button', { name: /Trova le tue foto/i }));
    await user.upload(
      screen.getByTestId('party-face-input'),
      new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' }),
    );
    await user.click(screen.getByTestId('party-face-submit'));
    await screen.findByTestId('party-face-count');
    await user.click(screen.getByTestId('party-face-show-results'));
    await waitFor(() => expect(screen.queryByTestId('party-face')).not.toBeInTheDocument());

    // Filtered: the header states a MATCH count, never "3" dressed up as the
    // album total, and the banner keeps the way back one tap away.
    expect(screen.getByTestId('party-gallery-count')).toHaveTextContent('1 foto trovata');
    expect(screen.getByTestId('party-gallery-count')).not.toHaveTextContent('3');
    expect(screen.getByTestId('party-face-filter')).toBeInTheDocument();
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile'))
      .toHaveLength(1);

    // And back to the whole album.
    await user.click(screen.getByTestId('party-face-filter-clear'));
    await waitFor(() =>
      expect(screen.getByTestId('party-gallery-count')).toHaveTextContent('3 elementi'));
    expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile'))
      .toHaveLength(3);
  });

  it('welcomes an empty album instead of stating a fact about it', async () => {
    mockAlbum([]);
    render(wrapper());
    const empty = await screen.findByTestId('party-empty');
    expect(empty).toHaveTextContent('Questo album non ha ancora foto.');
    expect(empty).toHaveTextContent('Sii il primo a condividere un momento.');
    // The nudge is text: the hero's CTA stays the single way to contribute.
    expect(within(empty).queryByRole('link')).not.toBeInTheDocument();
    expect(document.querySelectorAll('a[href="/party/upload-token/upload"]')).toHaveLength(1);
  });

  it('announces moments that arrive while the guest is on the page', async () => {
    let current = [media('f1'), media('f2')];
    installFetchMock({
      'GET /api/party/tok-1': () => jsonResponse({ ...hub, itemCount: current.length }),
      'GET /api/party/tok-1/items': () => jsonResponse({ albumName: 'Beach Party', items: current }),
    });
    vi.useFakeTimers();
    try {
      render(wrapper());
      await settle();
      // Nothing is "new" on arrival.
      expect(screen.queryByTestId('party-new-moments')).not.toBeInTheDocument();

      // Two guests upload; the poll appends them at the bottom, where the pill
      // is what makes them noticeable.
      current = [...current, media('f3'), media('f4')];
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getByTestId('party-new-moments')).toHaveTextContent('2 nuovi momenti');
      expect(screen.getByTestId('party-gallery-count')).toHaveTextContent('4 elementi');
      // The existing photos are still there, in order, and nothing scrolled.
      expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile'))
        .toHaveLength(4);

      // Acknowledging it clears the pill without touching the album.
      await act(async () => { screen.getByTestId('party-new-moments').click(); });
      expect(screen.queryByTestId('party-new-moments')).not.toBeInTheDocument();
      expect(screen.getByTestId('party-grid').querySelectorAll('button.party-guest-hub-tile'))
        .toHaveLength(4);
    } finally {
      vi.useRealTimers();
    }
  });

  it('the gallery grid CSS keeps a definite width and a definite column count (mobile overflow regression)', async () => {
    // jsdom performs no layout, so this guards the root cause statically. The
    // original bug: an auto-fill grid with no definite width resolved its track
    // count against max-width (68rem → 6 columns ≈ 940px) and overflowed
    // phones. The replacement keeps the definite width AND states its columns
    // outright, so the count can never be derived from the wrong box.
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const css = readFileSync(resolve(process.cwd(), 'src/pages/PartyGuestHub.css'), 'utf8');
    const rule = css.match(/\.party-guest-hub-tiles\s*\{([^}]*)\}/)?.[1] ?? '';
    expect(rule).toContain('width: 100%');
    expect(rule).toContain('min-width: 0');
    expect(rule).toContain('grid-template-columns: repeat(2, minmax(0, 1fr))');
    // And the legacy grid it replaced is gone rather than left behind.
    const legacy = readFileSync(resolve(process.cwd(), 'src/styles.css'), 'utf8');
    expect(legacy).not.toContain('.party-grid');
    expect(legacy).not.toContain('.party-lightbox');
  });
});

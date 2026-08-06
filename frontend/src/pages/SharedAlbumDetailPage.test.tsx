import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { SharedAlbumDetailPage } from './SharedAlbumDetailPage';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../test-utils';

// The shared wall lays out with the same justified geometry as the library
// wall, which needs a measured container width. jsdom reports 0 for every rect,
// so stub a width and a no-op ResizeObserver — the same convention
// MediaWorkspace.test.tsx uses — otherwise the wall stays pre-measurement.
beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({
      width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768,
      x: 0, y: 0, toJSON: () => ({}),
    }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const ALBUM = {
  albumId: 'alb-1',
  name: 'Vacanze',
  description: 'estate',
  ownerDisplayName: 'Alice',
  role: 'viewer' as const,
  allowOriginalDownload: false,
  itemCount: 2,
};

function item(over: Partial<Record<string, unknown>> = {}) {
  return {
    fileItemId: 'f1',
    kind: 'image',
    thumbnailUrl: '/api/shared-albums/alb-1/media/f1/thumbnail',
    previewUrl: '/api/shared-albums/alb-1/media/f1/preview',
    posterUrl: null,
    videoUrl: null,
    downloadUrl: null,
    width: 4000,
    height: 3000,
    addedAt: '2026-07-01T00:00:00Z',
    ...over,
  };
}

function renderPage(albumId = 'alb-1') {
  return render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/shared-albums/${albumId}`]}>
        <Routes>
          <Route path="/shared-albums/:albumId" element={<SharedAlbumDetailPage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

function mockAlbum(items: unknown[], album: Record<string, unknown> = ALBUM) {
  installFetchMock({
    'GET /api/shared-albums/alb-1': () => jsonResponse(album),
    'GET /api/shared-albums/alb-1/items': () => jsonResponse(items),
  });
}

describe('SharedAlbumDetailPage', () => {
  it('states that the album is live and owned by somebody else', async () => {
    mockAlbum([item()]);
    renderPage();

    expect(await screen.findByTestId('shared-album-owner')).toHaveTextContent('Alice');
    expect(screen.getByRole('heading', { name: 'Vacanze' })).toBeInTheDocument();
  });

  it('lays the wall out justified from each item’s real display ratio', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2', width: 3000, height: 4000 })]);
    renderPage();

    const tiles = await screen.findAllByTestId('shared-media-tile');
    expect(tiles).toHaveLength(2);

    // Justified: both tiles share a row height, and the landscape 4:3 tile is
    // wider than the portrait 3:4 one — the layout honours the real ratio
    // (EXIF quarter-turns already applied server-side) rather than cropping to
    // a uniform cell.
    const box = (el: HTMLElement) => ({
      w: parseFloat(el.style.width), h: parseFloat(el.style.height),
    });
    const [a, b] = tiles.map((t) => box(t as HTMLElement));
    expect(a.h).toBeCloseTo(b.h, 0);
    expect(a.w).toBeGreaterThan(b.w);
    expect(a.w / a.h).toBeCloseTo(4000 / 3000, 1);
    expect(b.w / b.h).toBeCloseTo(3000 / 4000, 1);
  });

  it('addresses every tile through the album-scoped route', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2', width: 3000, height: 4000 })]);
    renderPage();

    const tiles = await screen.findAllByTestId('shared-media-tile');
    for (const tile of tiles) {
      const src = tile.querySelector('img')?.getAttribute('src') ?? '';
      expect(src).toMatch(/^\/api\/shared-albums\/alb-1\/media\//);
      expect(src).not.toContain('/api/files/');
    }
  });

  it('opens the viewer and walks it with the arrow keys', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2', previewUrl: '/api/shared-albums/alb-1/media/f2/preview' })]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);

    const lightbox = await screen.findByTestId('shared-lightbox');
    expect(lightbox).toHaveTextContent('1 / 2');
    expect(screen.getByTestId('shared-lightbox-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f1/preview');

    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByTestId('shared-lightbox-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f2/preview');

    await userEvent.keyboard('{ArrowLeft}');
    expect(screen.getByTestId('shared-lightbox-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f1/preview');

    await userEvent.keyboard('{Escape}');
    expect(screen.queryByTestId('shared-lightbox')).not.toBeInTheDocument();
  });

  it('offers no download when the membership does not permit originals', async () => {
    mockAlbum([item()]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);

    expect(await screen.findByTestId('shared-lightbox')).toBeInTheDocument();
    expect(screen.queryByTestId('shared-download')).not.toBeInTheDocument();
  });

  it('offers the album-scoped download when the server supplied one', async () => {
    mockAlbum(
      [item({ downloadUrl: '/api/shared-albums/alb-1/media/f1/content' })],
      { ...ALBUM, allowOriginalDownload: true },
    );
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);

    const link = await screen.findByTestId('shared-download');
    expect(link).toHaveAttribute('href', '/api/shared-albums/alb-1/media/f1/content');
    // The album-scoped URL must not travel in a Referer header.
    expect(link).toHaveAttribute('rel', 'noreferrer');
  });

  it('plays a video through the album-scoped route, never the owner route', async () => {
    const VIDEO = '/api/shared-albums/alb-1/media/v1/video';
    const spy = installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(ALBUM),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([item({
        fileItemId: 'v1',
        kind: 'video',
        posterUrl: '/api/shared-albums/alb-1/media/v1/poster',
        videoUrl: VIDEO,
      })]),
      // The shared /video route speaks the SAME adaptive contract as the
      // owner's: a master playlist when the ladder is published.
      [`GET ${VIDEO}`]: () => new Response('#EXTM3U', {
        status: 200,
        headers: { 'content-type': 'application/vnd.apple.mpegurl' },
      }),
    });
    renderPage();

    const tile = (await screen.findAllByTestId('shared-media-tile'))[0];
    // The grid tile of a video is its poster.
    expect(tile.querySelector('img')).toHaveAttribute(
      'src', '/api/shared-albums/alb-1/media/v1/poster',
    );

    await userEvent.click(tile);

    // The player probes the ALBUM-SCOPED url. hls.js's MSE branch is not
    // reachable under jsdom, so what matters — and what a wrong URL would
    // break — is which route the player was pointed at.
    await vi.waitFor(() => {
      expect(spy.calls.some((c) => c.url === VIDEO)).toBe(true);
    });
    for (const call of spy.calls) {
      expect(call.url).not.toContain('/api/files/');
    }
  });

  it('shows a revoked/removed share as unavailable, not as an error', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => errorResponse(404),
      'GET /api/shared-albums/alb-1/items': () => errorResponse(404),
    });
    renderPage();

    expect(await screen.findByTestId('shared-album-unavailable')).toBeInTheDocument();
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('link', { name: /condivisi con me/i })).toBeInTheDocument();
  });

  it('offers a retry on a server error', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => errorResponse(500),
      'GET /api/shared-albums/alb-1/items': () => errorResponse(500),
    });
    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: /riprova/i })).toBeInTheDocument();
  });

  it('shows an empty album without breaking', async () => {
    mockAlbum([]);
    renderPage();

    expect(await screen.findByTestId('shared-album-empty')).toBeInTheDocument();
  });

  // SHARE-COPY-01: giving an album away is an OWNER-only act. An Editor may
  // curate the album, but curation is not redistribution — no role reaches this
  // affordance, and it must not exist even as a disabled control.
  it.each(['viewer', 'contributor', 'editor'])(
    'never offers "send a copy" to a %s',
    async (role) => {
      mockAlbum([item()], { ...ALBUM, role });
      renderPage();

      await screen.findByTestId('shared-album-page');
      expect(screen.queryByTestId('album-open-copy')).not.toBeInTheDocument();
      expect(screen.queryByTestId('album-copy-panel')).not.toBeInTheDocument();
      expect(document.body.innerHTML).not.toContain('Invia una copia');
    },
  );

  it('exposes no owner-library affordance anywhere on the page', async () => {
    mockAlbum([item()]);
    renderPage();

    await screen.findByTestId('shared-album-page');
    const html = document.body.innerHTML;
    // The recipient's viewer has no route into the owner's library or their
    // private semantic layer — those affordances do not exist in the component.
    for (const forbidden of ['/api/files/', '/people', '/media?', 'Simili', 'Persone']) {
      expect(html).not.toContain(forbidden);
    }
  });
});

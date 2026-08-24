import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { SharedAlbumDetailPage } from './SharedAlbumDetailPage';
import {
  AuthedWrapper, errorResponse, installFetchMock, jsonResponse, sharedItemsPage,
} from '../test-utils';

// The recipient's album, browsed with the SAME language as the owner's — kind
// tabs, one justified wall, one full-screen viewer, one Play — over a completely
// different authority. Every test here is about one of those two halves: the
// experience being the same, or the authority not being.

// The wall lays out with the justified geometry the library wall uses, which
// needs a measured container width. jsdom reports 0 for every rect, so stub a
// width and a no-op ResizeObserver — the same convention MediaWorkspace.test.tsx
// uses — otherwise the wall stays pre-measurement.
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
  version: 1,
  canEdit: false,
};

interface Item {
  fileItemId: string;
  kind: 'image' | 'video';
  thumbnailUrl: string;
  previewUrl: string;
  posterUrl: string | null;
  videoUrl: string | null;
  downloadUrl: string | null;
  albumItemId: string;
  width: number | null;
  height: number | null;
  addedAt: string;
  canWithdraw: boolean;
}

function item(over: Partial<Item> = {}): Item {
  const id = over.fileItemId ?? 'f1';
  return {
    fileItemId: id,
    kind: 'image',
    thumbnailUrl: `/api/shared-albums/alb-1/media/${id}/thumbnail`,
    previewUrl: `/api/shared-albums/alb-1/media/${id}/preview`,
    posterUrl: null,
    videoUrl: null,
    downloadUrl: null,
    albumItemId: `ai-${id}`,
    width: 4000,
    height: 3000,
    addedAt: '2026-07-01T00:00:00Z',
    canWithdraw: false,
    ...over,
  };
}

function video(over: Partial<Item> = {}): Item {
  const id = over.fileItemId ?? 'v1';
  return item({
    fileItemId: id,
    kind: 'video',
    posterUrl: `/api/shared-albums/alb-1/media/${id}/poster`,
    videoUrl: `/api/shared-albums/alb-1/media/${id}/video`,
    ...over,
  });
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

// The kind tab drives a server request, so the mock answers per kind exactly as
// the endpoint does.
function mockAlbum(items: Item[], album: Record<string, unknown> = ALBUM) {
  return installFetchMock({
    'GET /api/shared-albums/alb-1': () => jsonResponse(album),
    'GET /api/shared-albums/alb-1/items': (req) => {
      const kind = new URL(req.url, 'http://x').searchParams.get('kind');
      const slice = kind === null ? items : items.filter((i) => i.kind === kind);
      return jsonResponse({ ...(sharedItemsPage(slice) as object), total: items.length,
        photoCount: items.filter((i) => i.kind === 'image').length,
        videoCount: items.filter((i) => i.kind === 'video').length });
    },
  });
}

describe('SharedAlbumDetailPage identity', () => {
  it('states that the album is live and owned by somebody else', async () => {
    mockAlbum([item()]);
    renderPage();

    expect(await screen.findByTestId('shared-album-owner')).toHaveTextContent('Alice');
    expect(screen.getByRole('heading', { name: 'Vacanze' })).toBeInTheDocument();
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

  it('sends the way back to the shared collection of the one Albums page', async () => {
    mockAlbum([item()]);
    renderPage();

    const back = await screen.findByRole('link', { name: /condivisi con me/i });
    expect(back).toHaveAttribute('href', '/albums?scope=shared');
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
});

describe('SharedAlbumDetailPage wall', () => {
  it('lays the wall out justified from each item’s real display ratio', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2', width: 3000, height: 4000 })]);
    renderPage();

    const tiles = await screen.findAllByTestId('shared-media-tile');
    expect(tiles).toHaveLength(2);

    // Justified: both tiles share a row height, and the landscape 4:3 tile is
    // wider than the portrait 3:4 one — the layout honours the real ratio
    // (EXIF quarter-turns already applied server-side) rather than cropping to
    // a uniform cell. The SAME geometry the owner's wall uses.
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

  it('carries no file name into the wall or the viewer', async () => {
    // The item shape has no name at all; this asserts that nothing here invents
    // one from an id or a URL segment.
    mockAlbum([item()]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    const title = await screen.findByTestId('media-viewer-title');
    expect(title.textContent).toMatch(/^Elemento 1 di 1$/);
  });
});

describe('SharedAlbumDetailPage kind filter', () => {
  it('offers All / Photos / Videos with the album’s own counts', async () => {
    mockAlbum([item(), video()]);
    renderPage();

    // Wait for the first page: the tabs render before it lands, with the count
    // slot reserved and empty, so asserting too early reads the placeholder.
    expect(await screen.findAllByTestId('shared-media-tile')).toHaveLength(2);
    expect(screen.getByTestId('media-kind-count-all')).toHaveTextContent('2');
    expect(screen.getByTestId('media-kind-count-image')).toHaveTextContent('1');
    expect(screen.getByTestId('media-kind-count-video')).toHaveTextContent('1');
  });

  it('asks the server for the chosen kind rather than filtering locally', async () => {
    const spy = mockAlbum([item(), video()]);
    renderPage();

    await userEvent.click(await screen.findByTestId('media-kind-tab-video'));

    await vi.waitFor(() => {
      expect(spy.calls.some((c) => c.url.includes('kind=video'))).toBe(true);
    });
    const tiles = await screen.findAllByTestId('shared-media-tile');
    expect(tiles).toHaveLength(1);
    expect(tiles[0].querySelector('img'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/v1/poster');
  });
});

describe('SharedAlbumDetailPage viewer', () => {
  it('opens the viewer and walks it with the arrow keys', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2' })]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);

    await screen.findByTestId('media-viewer');
    expect(screen.getByTestId('media-viewer-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f1/preview');

    await userEvent.keyboard('{ArrowRight}');
    expect(screen.getByTestId('media-viewer-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f2/preview');

    await userEvent.keyboard('{ArrowLeft}');
    expect(screen.getByTestId('media-viewer-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f1/preview');

    await userEvent.keyboard('{Escape}');
    expect(screen.queryByTestId('media-viewer')).not.toBeInTheDocument();
  });

  it('uses the URLs the server supplied and asks for no metadata document', async () => {
    const spy = mockAlbum([item()]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    await screen.findByTestId('media-viewer');

    // Every byte the viewer shows comes from a URL the SERVER built. An
    // <img src> never reaches the fetch spy, so the element itself is asserted:
    // synthesizing `/api/files/{id}/preview` from the file id would address the
    // owner's library, which this caller holds no grant on.
    expect(screen.getByTestId('media-viewer-image').getAttribute('src'))
      .toMatch(/^\/api\/shared-albums\/alb-1\/media\//);

    // The owner's metadata endpoint is not reachable from a share, and the
    // recipient's viewer does not even try: no request, and no details drawer.
    for (const call of spy.calls) {
      expect(call.url).not.toContain('/api/files/');
      expect(call.url).not.toContain('/metadata');
    }
    expect(screen.queryByTestId('viewer-details-toggle')).not.toBeInTheDocument();
  });

  it('offers no download when the membership does not permit originals', async () => {
    mockAlbum([item()]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);

    expect(await screen.findByTestId('media-viewer')).toBeInTheDocument();
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
      'GET /api/shared-albums/alb-1/items': () => jsonResponse(sharedItemsPage([video()])),
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
});

describe('SharedAlbumDetailPage Play', () => {
  it('starts the album from the first item', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2' })]);
    renderPage();

    await userEvent.click(await screen.findByTestId('album-play'));

    expect(await screen.findByTestId('media-viewer-image'))
      .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f1/preview');
    // A run in progress can be stopped from inside the viewer.
    expect(screen.getByTestId('viewer-play-stop')).toBeInTheDocument();
  });

  it('advances a photo on its own and stops at the last item', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      mockAlbum([item(), item({ fileItemId: 'f2' })]);
      renderPage();

      const play = await screen.findByTestId('album-play');
      await userEvent.click(play);
      await screen.findByTestId('media-viewer-image');

      await vi.advanceTimersByTimeAsync(6000);
      expect(screen.getByTestId('media-viewer-image'))
        .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f2/preview');

      // The end: the run stops on the last item rather than closing the viewer
      // out from under the person watching, and offers to run it again.
      await vi.advanceTimersByTimeAsync(6000);
      expect(await screen.findByTestId('viewer-play-replay')).toBeInTheDocument();
      expect(screen.getByTestId('media-viewer-image'))
        .toHaveAttribute('src', '/api/shared-albums/alb-1/media/f2/preview');
    } finally {
      vi.useRealTimers();
    }
  });

  it('plays the FILTERED sequence, never the hidden rest of the album', async () => {
    mockAlbum([item(), item({ fileItemId: 'f2' }), video()]);
    renderPage();

    // Videos only, then Play: the first thing shown must be the video, not the
    // album's first photo.
    await userEvent.click(await screen.findByTestId('media-kind-tab-video'));
    await vi.waitFor(async () => {
      expect(await screen.findAllByTestId('shared-media-tile')).toHaveLength(1);
    });
    await userEvent.click(screen.getByTestId('album-play'));

    await screen.findByTestId('media-viewer');
    // The sequence is the ONE video, not the album's three items: no photo is
    // on screen, and the viewer's own position line says how long the sequence
    // it is playing actually is.
    expect(screen.queryByTestId('media-viewer-image')).not.toBeInTheDocument();
    expect(screen.getByTestId('media-viewer-title')).toHaveTextContent('Elemento 1 di 1');
  });
});

describe('SharedAlbumDetailPage authority', () => {
  // Giving an album away is an OWNER-only act. An Editor may curate the album,
  // but curation is not redistribution — no role reaches this affordance, and it
  // must not exist even as a disabled control.
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

  it('offers a Viewer no owner mutation of any kind', async () => {
    mockAlbum([item()]);
    renderPage();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    await screen.findByTestId('media-viewer');

    for (const forbidden of [
      'media-select-control', 'ws-selection-bar', 'album-open-settings', 'album-open-share',
      'shared-album-edit', 'shared-album-curate', 'shared-album-add', 'album-delete-btn',
      'viewer-details-toggle', 'shared-withdraw',
    ]) {
      expect(screen.queryByTestId(forbidden)).not.toBeInTheDocument();
    }
  });

  it('offers curation only when the SERVER says this caller may edit', async () => {
    mockAlbum([item()], { ...ALBUM, role: 'editor', canEdit: false });
    renderPage();

    await screen.findByTestId('shared-album-page');
    // The label says Editor; the server said no. The server wins.
    expect(screen.queryByTestId('shared-album-edit')).not.toBeInTheDocument();
    expect(screen.queryByTestId('shared-album-curate')).not.toBeInTheDocument();

    cleanup();
    mockAlbum([item()], { ...ALBUM, role: 'editor', canEdit: true });
    renderPage();
    expect(await screen.findByTestId('shared-album-edit')).toBeInTheDocument();
    expect(screen.getByTestId('shared-album-curate')).toBeInTheDocument();
  });
});

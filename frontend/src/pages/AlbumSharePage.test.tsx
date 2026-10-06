import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { AlbumSharePage } from './AlbumSharePage';
import { HomeScreenAppCanonical, homeScreenAppNavigation } from '../homeScreen/homeScreen';

/**
 * THE PUBLIC PAGE somebody opens from a message.
 *
 * What these defend:
 *   * look, take, add — and NOTHING that removes. There is no delete control,
 *     because there is no route behind one;
 *   * the grid asks for SMALL and the viewer for the preview, as every other
 *     surface in this product does;
 *   * a link whose owner closed contribution still opens, and simply does not
 *     offer to add;
 *   * the ceiling is reported as the link's, and reaching it is explained
 *     rather than shown as a failure;
 *   * a protected link says it is protected instead of pretending to be
 *     nothing, because the visitor is expected;
 *   * the page states the browser limit it cannot beat — closing the tab stops
 *     the upload — rather than letting somebody walk away and lose photographs.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  window.history.replaceState(null, '', '/');
  document.title = '';
  document.head.innerHTML = '';
});

const TOKEN = 'a-share-token';

function album(over: Record<string, unknown> = {}) {
  return {
    albumName: 'Vacanze',
    coverUrl: null,
    itemCount: 2,
    canUpload: true,
    canDownloadOriginal: false,
    uploadsRemaining: 5,
    ...over,
  };
}

function items() {
  return {
    items: [
      {
        id: 'i1',
        thumbnailUrl: `/api/album-share/${TOKEN}/media/i1/thumbnail`,
        previewUrl: `/api/album-share/${TOKEN}/media/i1/preview`,
        downloadUrl: `/api/album-share/${TOKEN}/media/i1/download`,
        playbackUrl: null,
        isVideo: false,
      },
      {
        id: 'i2',
        thumbnailUrl: `/api/album-share/${TOKEN}/media/i2/thumbnail`,
        previewUrl: `/api/album-share/${TOKEN}/media/i2/preview`,
        // A video on a link whose owner has not allowed originals: there is no
        // safe rendition to hand over, so the server sends no URL at all.
        downloadUrl: null,
        // Playback is offered even with originals off: HLS is a transcoded
        // ladder, never the camera's file.
        playbackUrl: `/api/album-share/${TOKEN}/media/i2/video`,
        isVideo: true,
      },
    ],
    nextCursor: null,
  };
}

function mount() {
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[`/album/${TOKEN}`]}>
        <Routes><Route path="/album/:token" element={<AlbumSharePage />} /></Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

function serve(over: Record<string, unknown> = {}, itemsOver = items()) {
  return installFetchMock({
    [`GET /api/album-share/${TOKEN}`]: () => jsonResponse(album(over)),
    [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(itemsOver),
  });
}

it('shows the album, and offers no way to remove anything from it', async () => {
  serve();
  mount();

  expect(await screen.findByText('Vacanze')).toBeInTheDocument();
  expect(screen.getByTestId('album-share-item-i1')).toBeInTheDocument();
  expect(screen.getByTestId('album-share-item-i2')).toBeInTheDocument();

  // The one power an owner cannot lend by accident.
  expect(screen.queryByRole('button', { name: /elimina|cancella|rimuovi/i })).toBeNull();
});

it('opens a photograph at preview size and hands it over on request', async () => {
  const user = userEvent.setup();
  serve();
  mount();

  await user.click(await screen.findByTestId('album-share-item-i1'));
  const viewer = await screen.findByTestId('public-viewer');
  // The grid asked for the SMALL rendition; the viewer asks for the preview.
  expect(viewer.querySelector('img')).toHaveAttribute(
    'src', `/api/album-share/${TOKEN}/media/i1/preview`);

  const download = screen.getByTestId('public-viewer-download');
  expect(download).toHaveAttribute('href', `/api/album-share/${TOKEN}/media/i1/download`);
  // The owner left originals off, so the button does not promise one.
  expect(download).toHaveTextContent(/^Scarica$/);
});

it("lays the album out as the party's mosaic, and walks it in the viewer", async () => {
  const user = userEvent.setup();
  serve();
  mount();

  const grid = await screen.findByTestId('album-share-grid');
  expect(grid).toHaveClass('public-gallery');
  expect(grid.querySelectorAll('button.public-gallery-tile')).toHaveLength(2);
  expect(grid.querySelector('.public-gallery-tile-play')).toBeInTheDocument();

  await user.click(screen.getByTestId('album-share-item-i1'));
  await user.click(screen.getByRole('button', { name: 'Successivo' }));
  expect(screen.getByRole('dialog', { name: 'Visualizzatore video' })).toBeInTheDocument();
  await user.click(screen.getByRole('button', { name: 'Precedente' }));
  expect(screen.getByRole('dialog', { name: 'Visualizzatore foto' })).toBeInTheDocument();

  // Closing hands focus back to the tile it was opened from.
  await user.click(screen.getByTestId('public-viewer-close'));
  expect(document.activeElement).toBe(screen.getByTestId('album-share-item-i1'));
});

it('plays a video through the adaptive player, and says why it cannot be taken', async () => {
  const user = userEvent.setup();
  const { calls } = installFetchMock({
    [`GET /api/album-share/${TOKEN}`]: () => jsonResponse(album()),
    [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
    // The ladder is still being prepared: the poster, and a status.
    [`GET /api/album-share/${TOKEN}/media/i2/video`]: () =>
      new Response(null, { status: 202, headers: { 'Retry-After': '30' } }),
  });
  mount();

  await user.click(await screen.findByTestId('album-share-item-i2'));
  // The ladder, not the original — which is why it is offered at all — asked
  // for by the same player the owner's library uses.
  await waitFor(() => expect(calls.some((c) => c.url === `/api/album-share/${TOKEN}/media/i2/video`)).toBe(true));
  expect(screen.queryByTestId('public-viewer-stage')).toBeNull();
  await waitFor(() => expect(screen.getByTestId('public-viewer').querySelector('img'))
    .toHaveAttribute('src', `/api/album-share/${TOKEN}/media/i2/preview`));

  // And no download button, because the route would have answered 404.
  expect(screen.queryByTestId('public-viewer-download')).toBeNull();
  expect(screen.getByText(/si può solo guardare qui/i)).toBeInTheDocument();
});

it('says when the owner allowed the real file', async () => {
  const user = userEvent.setup();
  serve({ canDownloadOriginal: true });
  mount();

  await user.click(await screen.findByTestId('album-share-item-i1'));
  expect(screen.getByTestId('public-viewer-download')).toHaveTextContent(/originale/i);
});

it('still opens when the owner closed contribution', async () => {
  serve({ canUpload: false });
  mount();

  expect(await screen.findByText('Vacanze')).toBeInTheDocument();
  expect(screen.getByTestId('album-share-grid')).toBeInTheDocument();
  // Reading survives; only the second switch moved.
  expect(screen.queryByTestId('album-share-upload')).toBeNull();
});

it('reports the ceiling as the link’s, and warns before somebody walks away', async () => {
  serve({ uploadsRemaining: 3 });
  mount();

  expect(await screen.findByTestId('album-share-remaining')).toHaveTextContent('3');
  // The browser limit, said out loud: an upload does not survive the tab
  // closing, and a visitor who assumes it does loses photographs.
  expect(screen.getByText(/tieni questa pagina aperta/i)).toBeInTheDocument();
});

it('a closed or unknown link is one generic nothing', async () => {
  installFetchMock({
    [`GET /api/album-share/${TOKEN}`]: () => new Response('no', { status: 404 }),
  });
  mount();

  expect(await screen.findByTestId('album-share-gone')).toBeInTheDocument();
  expect(screen.queryByTestId('album-share-grid')).toBeNull();
});

it('a protected link asks who you are instead of pretending to be nothing', async () => {
  const user = userEvent.setup();
  const asked: unknown[] = [];
  let verified = false;
  installFetchMock({
    [`GET /api/album-share/${TOKEN}`]: () => (verified
      ? jsonResponse(album())
      : new Response(JSON.stringify({ error: 'second_factor_required' }), { status: 401 })),
    [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
    [`POST /api/album-share/${TOKEN}/challenge`]: ({ body }: { body: string | null }) => {
      asked.push(JSON.parse(body ?? '{}'));
      return new Response(null, { status: 202 });
    },
    [`POST /api/album-share/${TOKEN}/verify`]: () => {
      verified = true;
      return new Response(null, { status: 204 });
    },
  });
  mount();

  const gate = await screen.findByTestId('album-share-gate');
  expect(gate).toBeInTheDocument();

  await user.type(screen.getByTestId('album-share-email'), 'zia@example.com');
  await user.click(screen.getByTestId('album-share-ask'));

  await waitFor(() => expect(asked).toHaveLength(1));
  // The screen says the same thing whether or not the address is on the list,
  // because the server does — otherwise the link reads out the guest list.
  expect(await screen.findByTestId('album-share-sent')).toHaveTextContent(/se il tuo indirizzo/i);

  await user.type(screen.getByTestId('album-share-code'), '123456');
  await user.click(screen.getByTestId('album-share-verify'));

  expect(await screen.findByText('Vacanze')).toBeInTheDocument();
});

// ── The album's own home-screen app ────────────────────────────────────────

describe('inside the album\'s own app', () => {
  const APP = '/album/app/1111111111111111aaaaaaaaaaaaaaaa';
  const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1';

  function mountInApp() {
    render(
      <I18nProvider>
        <MemoryRouter basename={APP} initialEntries={[`${APP}/open/${TOKEN}`]}>
          <Routes><Route path="/open/:token" element={<AlbumSharePage />} /></Routes>
        </MemoryRouter>
      </I18nProvider>,
    );
  }

  it('is the same page, asking the same API, with every picture and download where it always was', async () => {
    const { calls } = serve();
    mountInApp();

    expect(await screen.findByText('Vacanze')).toBeInTheDocument();
    expect(screen.getByTestId('album-share-item-i1').querySelector('img'))
      .toHaveAttribute('src', `/api/album-share/${TOKEN}/media/i1/thumbnail`);
    expect(calls.map((c) => c.url)).toEqual([`/api/album-share/${TOKEN}`, `/api/album-share/${TOKEN}/items`]);

    await userEvent.click(screen.getByTestId('album-share-item-i1'));
    expect(screen.getByRole('link', { name: /scarica/i }))
      .toHaveAttribute('href', `/api/album-share/${TOKEN}/media/i1/download`);
  });

  it('names the page and the app after the album, and offers to install it', async () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(IPHONE);
    document.head.innerHTML = '<meta name="apple-mobile-web-app-title" content="NubArca">';
    serve();
    mountInApp();

    expect(await screen.findByTestId('album-home-button')).toHaveTextContent('Installa album');
    expect(document.title).toBe('Vacanze');
    expect(document.head.querySelector('meta[name="apple-mobile-web-app-title"]')).toHaveAttribute('content', 'Vacanze');
  });

  it('offers nothing to install while the album is not open', async () => {
    vi.spyOn(navigator, 'userAgent', 'get').mockReturnValue(IPHONE);
    installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: () => new Response(JSON.stringify({ error: 'second_factor_required' }), { status: 401 }),
    });
    mountInApp();

    expect(await screen.findByTestId('album-share-gate')).toBeInTheDocument();
    expect(screen.queryByTestId('album-home-button')).toBeNull();
  });

  it('shows another album\'s token as unavailable, asking the album for nothing', async () => {
    window.history.replaceState(null, '', `${APP}/open/${TOKEN}`);
    const replace = vi.spyOn(homeScreenAppNavigation, 'replace').mockImplementation(() => {});
    const { calls } = installFetchMock({
      [`GET /api/album-share/${TOKEN}/app-manifest`]: () => jsonResponse({
        id: '/album/app/2222222222222222bbbbbbbbbbbbbbbb',
        scope: '/album/app/2222222222222222bbbbbbbbbbbbbbbb/',
      }),
    });
    render(
      <HomeScreenAppCanonical>
        <I18nProvider>
          <MemoryRouter basename={APP} initialEntries={[`${APP}/open/${TOKEN}`]}>
            <Routes><Route path="/open/:token" element={<AlbumSharePage />} /></Routes>
          </MemoryRouter>
        </I18nProvider>
      </HomeScreenAppCanonical>,
    );

    expect(await screen.findByTestId('album-share-gone')).toBeInTheDocument();
    expect(screen.queryByText('Vacanze')).toBeNull();
    expect(calls.map((c) => c.url)).toEqual([`/api/album-share/${TOKEN}/app-manifest`]);
    expect(replace).not.toHaveBeenCalled();
  });

  it('starts again once a protected album is verified, so the app is resolved with the grant', async () => {
    const user = userEvent.setup();
    window.history.replaceState(null, '', `/album/${TOKEN}`);
    const reload = vi.spyOn(homeScreenAppNavigation, 'reload').mockImplementation(() => {});
    installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: () =>
        new Response(JSON.stringify({ error: 'second_factor_required' }), { status: 401 }),
      [`POST /api/album-share/${TOKEN}/challenge`]: () => new Response(null, { status: 202 }),
      [`POST /api/album-share/${TOKEN}/verify`]: () => new Response(null, { status: 204 }),
    });
    mount();

    await screen.findByTestId('album-share-gate');
    await user.type(screen.getByTestId('album-share-email'), 'zia@example.com');
    await user.click(screen.getByTestId('album-share-ask'));
    await screen.findByTestId('album-share-sent');
    await user.type(screen.getByTestId('album-share-code'), '123456');
    await user.click(screen.getByTestId('album-share-verify'));

    await waitFor(() => expect(reload).toHaveBeenCalledTimes(1));
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { createMemoryRouter, MemoryRouter, Route, RouterProvider, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { AlbumSharePage } from './AlbumSharePage';
import { HomeScreenAppCanonical, homeScreenAppNavigation } from '../homeScreen/homeScreen';
import { MockUploadXhr } from '../test-utils/MockUploadXhr';
import { fileKey, loadDone, markDone } from '../uploads/uploadQueueStore';

vi.mock('../uploads/uploadQueueStore', async (importOriginal) => ({
  ...await importOriginal<typeof import('../uploads/uploadQueueStore')>(),
  loadDone: vi.fn(async () => new Set<string>()),
  markDone: vi.fn(async () => {}),
}));

beforeEach(() => {
  MockUploadXhr.sent = [];
  vi.stubGlobal('XMLHttpRequest', MockUploadXhr);
  vi.mocked(loadDone).mockReset().mockResolvedValue(new Set());
  vi.mocked(markDone).mockReset().mockResolvedValue(undefined);
});

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
  Reflect.deleteProperty(navigator, 'wakeLock');
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

function items(token = TOKEN) {
  return {
    items: [
      {
        id: 'i1',
        thumbnailUrl: `/api/album-share/${token}/media/i1/thumbnail`,
        previewUrl: `/api/album-share/${token}/media/i1/preview`,
        downloadUrl: `/api/album-share/${token}/media/i1/download`,
        playbackUrl: null,
        isVideo: false,
      },
      {
        id: 'i2',
        thumbnailUrl: `/api/album-share/${token}/media/i2/thumbnail`,
        previewUrl: `/api/album-share/${token}/media/i2/preview`,
        // A video on a link whose owner has not allowed originals: there is no
        // safe rendition to hand over, so the server sends no URL at all.
        downloadUrl: null,
        // Playback is offered even with originals off: HLS is a transcoded
        // ladder, never the camera's file.
        playbackUrl: `/api/album-share/${token}/media/i2/video`,
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

// Uploads must show the difference between bytes transferred and files saved.
describe('upload feedback and recovery', () => {
  const photos = () => [
    new File(['one'], 'one.jpg', { type: 'image/jpeg', lastModified: 1 }),
    new File(['two'], 'two.jpg', { type: 'image/jpeg', lastModified: 2 }),
  ];
  async function pick(files = photos()) {
    await userEvent.upload(await screen.findByTestId('album-share-input'), files);
    await waitFor(() => expect(MockUploadXhr.sent.length).toBeGreaterThan(0));
  }
  async function finish(index = 0, report?: unknown, status?: number) {
    await act(async () => { MockUploadXhr.sent[index].finish(report, status); });
  }

  it('offers a named custom picker and the album cover above the gallery', async () => {
    serve({ coverUrl: '/cover-preview' });
    mount();
    expect(await screen.findByRole('button', { name: 'Scegli foto e video' })).toBeEnabled();
    expect(screen.getByText('Album condiviso')).toBeInTheDocument();
    expect(document.querySelector('.album-share-cover')).toHaveAttribute('src', '/cover-preview');
    expect(screen.getByTestId('album-share-input')).toHaveClass('party-contribution-file-input');
  });

  it('shows real per-file progress, waits at 100%, then sends the next file', async () => {
    serve(); mount(); await pick();
    expect(screen.getByRole('button', { name: 'Caricamento in corso' })).toBeDisabled();
    expect(MockUploadXhr.sent).toHaveLength(1);
    act(() => MockUploadXhr.sent[0].progress(50, 100));
    expect(screen.getByRole('progressbar')).toHaveAttribute('aria-valuenow', '50');
    expect(screen.getByText('File 1 di 2 · 50%')).toBeInTheDocument();
    act(() => MockUploadXhr.sent[0].upload.onload?.());
    expect(screen.getByText('Salvataggio in corso')).toBeInTheDocument();
    expect(screen.getByText('0 di 2 file salvati')).toBeInTheDocument();
    expect(screen.queryByText('Caricamento completato')).toBeNull();
    expect(screen.getByText(/Attendi qui/)).toBeInTheDocument();
    await finish();
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(2));
    expect(screen.getByText('1 di 2 file salvati')).toBeInTheDocument();
    await finish(1);
    expect(await screen.findByText('Caricamento completato')).toBeInTheDocument();
    expect(screen.getByText('2 di 2 file salvati')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Scegli foto e video' })).toBeEnabled();
  });

  it('uploads both distinct files with identical resume metadata', async () => {
    const contents = (file: FormDataEntryValue | null | undefined) => new Promise<string>((resolve) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.readAsText(file as Blob);
    });
    const first = new File(['one'], 'same.jpg', { type: 'image/jpeg', lastModified: 123 });
    const second = new File(['two'], 'same.jpg', { type: 'image/jpeg', lastModified: 123 });
    expect(first).not.toBe(second);
    expect(fileKey(first)).toBe(fileKey(second));
    serve(); mount(); await pick([first, second]);
    expect(await contents(MockUploadXhr.sent[0].body?.get('file'))).toBe('one');
    await finish();
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(2));
    expect(await contents(MockUploadXhr.sent[1].body?.get('file'))).toBe('two');
    await finish(1);
    expect(await screen.findByText('Caricamento completato')).toBeInTheDocument();
    expect(screen.getByTestId('album-share-progress')).toHaveTextContent('2 di 2 file salvati');
    expect(screen.queryByText(/saltat/i)).toBeNull();
    expect(MockUploadXhr.sent.map(request => request.method)).toEqual(['POST', 'POST']);
    expect(markDone).toHaveBeenCalledTimes(2);
  });

  it('warns before closing only while an upload is active', async () => {
    serve(); mount(); await pick([photos()[0]]);
    const active = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(active);
    expect(active.defaultPrevented).toBe(true);
    await finish();
    const idle = new Event('beforeunload', { cancelable: true });
    window.dispatchEvent(idle);
    expect(idle.defaultPrevented).toBe(false);
  });

  it('keeps the final confirmation even when the last available slot closes uploads', async () => {
    let refreshed = false;
    installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: () => jsonResponse(album(refreshed ? { canUpload: false, uploadsRemaining: 0 } : {})),
      [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
    });
    mount(); await pick([photos()[0]]);
    refreshed = true;
    await finish();
    expect(await screen.findByText('Caricamento completato')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByRole('button', { name: 'Scegli foto e video' })).toBeDisabled());
    expect(screen.getByTestId('album-share-progress')).toHaveTextContent('1 di 1 file salvati');
  });

  it('does not claim success or remember a file for an invalid success response', async () => {
    serve(); mount(); await pick();
    await finish(0, '<html>proxy response</html>');
    expect(await screen.findByText('Caricamento da completare')).toBeInTheDocument();
    expect(markDone).not.toHaveBeenCalled();
    expect(MockUploadXhr.sent).toHaveLength(1);
    expect(screen.getByText(/Controlla l’album prima/)).toBeInTheDocument();
    expect(screen.getByText('1 file ancora da inviare')).toBeInTheDocument();
  });

  it('stops on a quota report, preserves the result through refresh, and does not call it a failed file', async () => {
    let refreshed = false;
    installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: () => jsonResponse(album(refreshed ? { canUpload: false, uploadsRemaining: 0 } : {})),
      [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
    });
    mount(); await pick(); refreshed = true;
    await finish(0, { accepted: 0, rejected: 0, stopped: 'upload_limit_reached' });
    expect(await screen.findByTestId('album-share-limit')).toBeInTheDocument();
    expect(screen.getByText('2 file ancora da inviare')).toBeInTheDocument();
    expect(screen.queryByText(/file senza conferma/)).toBeNull();
    expect(MockUploadXhr.sent).toHaveLength(1);
    expect(markDone).not.toHaveBeenCalled();
  });

  it.each([
    [429, /Troppi caricamenti/], [401, /L’accesso è cambiato/], [404, /L’accesso è cambiato/],
    [503, /Connessione interrotta/],
  ])('stops the queue after HTTP %s', async (status, message) => {
    serve(); mount(); await pick();
    await finish(0, {}, status as number);
    expect(await screen.findByText(message as RegExp)).toBeInTheDocument();
    expect(MockUploadXhr.sent).toHaveLength(1);
    expect(screen.queryByText('Caricamento completato')).toBeNull();
  });

  it('keeps successful files saved and retries only the missing files after network loss', async () => {
    serve(); mount(); await pick(); await finish();
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(2));
    await act(async () => MockUploadXhr.sent[1].onerror?.());
    expect(await screen.findByText('Caricamento da completare')).toBeInTheDocument();
    expect(screen.getByText('1 di 2 file salvati')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Riprova i file mancanti' }));
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(3));
    expect((MockUploadXhr.sent[2].body?.get('file') as File).name).toBe('two.jpg');
    await finish(2);
    expect(await screen.findByText('Caricamento completato')).toBeInTheDocument();
    expect(markDone).toHaveBeenCalledTimes(2);
  });

  it('does not discard the result when refreshing the gallery fails', async () => {
    let broken = false;
    installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: () => broken ? new Response(null, { status: 503 }) : jsonResponse(album()),
      [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
    });
    mount(); await pick([photos()[0]]); broken = true;
    await finish();
    expect(await screen.findByText(/Non riesco ad aggiornare le foto/)).toBeInTheDocument();
    expect(screen.getByText('Caricamento completato')).toBeInTheDocument();
    expect(screen.getByTestId('album-share-grid')).toBeInTheDocument();
    expect(screen.queryByTestId('album-share-gone')).toBeNull();
  });

  it('skips confirmed files on a new selection even if browser storage refuses writes', async () => {
    vi.mocked(markDone).mockRejectedValue(new Error('storage denied'));
    serve(); mount(); const files = photos(); await pick([files[0]]); await finish();
    expect(await screen.findByText('Caricamento completato')).toBeInTheDocument();
    await userEvent.upload(screen.getByTestId('album-share-input'), files);
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(2));
    expect((MockUploadXhr.sent[1].body?.get('file') as File).name).toBe('two.jpg');
    await finish(1);
    expect(await screen.findByText('1 già caricata, saltata')).toBeInTheDocument();
  });

  it('resumes a previous visit by skipping only files with a stored confirmation', async () => {
    const files = photos();
    vi.mocked(loadDone).mockResolvedValue(new Set([fileKey(files[0])]));
    serve(); mount(); await pick(files);
    expect(MockUploadXhr.sent).toHaveLength(1);
    expect((MockUploadXhr.sent[0].body?.get('file') as File).name).toBe('two.jpg');
    await finish();
    expect(await screen.findByText('1 già caricata, saltata')).toBeInTheDocument();
    expect(screen.getByText('1 di 1 file salvati')).toBeInTheDocument();
  });

  it('releases wake lock and aborts the active request on unmount without starting another file', async () => {
    const release = vi.fn(async () => {});
    Object.defineProperty(navigator, 'wakeLock', { configurable: true, value: {
      request: vi.fn(async () => ({ released: false, release, addEventListener: vi.fn() })),
    } });
    serve(); mount(); await pick();
    cleanup();
    await waitFor(() => expect(release).toHaveBeenCalledTimes(1));
    expect(MockUploadXhr.sent[0].aborted).toBe(true);
    expect(MockUploadXhr.sent).toHaveLength(1);
  });

  it('distinguishes temporary opening failures from a revoked link and lets the visitor retry', async () => {
    let broken = true;
    installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: () => broken ? new Response(null, { status: 503 }) : jsonResponse(album()),
      [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
    });
    mount();
    expect(await screen.findByText(/Non riesco ad aprire l’album/)).toBeInTheDocument();
    expect(screen.queryByTestId('album-share-gone')).toBeNull();
    broken = false;
    await userEvent.click(screen.getByRole('button', { name: 'Riprova' }));
    expect(await screen.findByTestId('album-share-grid')).toBeInTheDocument();
  });
});

describe('moving between shared album links', () => {
  const OTHER = 'another-share-token';
  const otherAlbum = () => album({ albumName: 'Montagna', coverUrl: '/mountain-cover', uploadsRemaining: 2, itemCount: 1 });
  const otherItems = () => ({ items: [{ ...items(OTHER).items[0], id: 'mountain-photo' }], nextCursor: null });
  function deferredResponse() {
    let resolve!: (response: Response) => void;
    const promise = new Promise<Response>((complete) => { resolve = complete; });
    return { promise, resolve };
  }
  function mountRoutes() {
    const router = createMemoryRouter([{ path: '/album/:token', element: <AlbumSharePage /> }], {
      initialEntries: [`/album/${TOKEN}`],
    });
    render(<I18nProvider><RouterProvider router={router} /></I18nProvider>);
    return router;
  }
  function serveRoutes(
    first: () => Response | Promise<Response> = () => jsonResponse(album()),
    second: () => Response | Promise<Response> = () => jsonResponse(otherAlbum()),
  ) {
    return installFetchMock({
      [`GET /api/album-share/${TOKEN}`]: first,
      [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse(items()),
      [`GET /api/album-share/${OTHER}`]: second,
      [`GET /api/album-share/${OTHER}/items`]: () => jsonResponse(otherItems()),
    });
  }

  it('hides the old album and aborts its upload while the next link is pending', async () => {
    const next = deferredResponse();
    serveRoutes(undefined, () => next.promise);
    const router = mountRoutes();
    await screen.findByText('Vacanze');
    await userEvent.upload(screen.getByTestId('album-share-input'), new File(['one'], 'one.jpg', { type: 'image/jpeg' }));
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(1));
    await act(async () => { await router.navigate(`/album/${OTHER}`); });
    expect(screen.queryByText('Vacanze')).toBeNull();
    expect(screen.queryByTestId('album-share-grid')).toBeNull();
    expect(screen.queryByTestId('album-share-input')).toBeNull();
    expect(screen.queryByTestId('album-share-remaining')).toBeNull();
    expect(screen.queryByRole('button', { name: 'Scegli foto e video' })).toBeNull();
    expect(MockUploadXhr.sent[0].aborted).toBe(true);
  });

  it('ignores a late initial response from the previous link after the new album is ready', async () => {
    const old = deferredResponse();
    const { calls } = serveRoutes(() => old.promise);
    const router = mountRoutes();
    await waitFor(() => expect(calls.some(call => call.url === `/api/album-share/${TOKEN}`)).toBe(true));
    await act(async () => { await router.navigate(`/album/${OTHER}`); });
    await screen.findByText('Montagna');
    // The stub deliberately delivers despite the old request's aborted signal.
    await act(async () => { old.resolve(jsonResponse(album())); });
    expect(screen.getByText('Montagna')).toBeInTheDocument();
    expect(screen.queryByText('Vacanze')).toBeNull();
    expect(screen.getByTestId('album-share-item-mountain-photo')).toBeInTheDocument();
  });

  it('shows the new title, cover, quota and items and uploads only to the displayed link', async () => {
    const next = deferredResponse();
    serveRoutes(undefined, () => next.promise);
    const router = mountRoutes();
    await screen.findByText('Vacanze');
    await act(async () => { await router.navigate(`/album/${OTHER}`); });
    await act(async () => { next.resolve(jsonResponse(otherAlbum())); });
    expect(await screen.findByText('Montagna')).toBeInTheDocument();
    expect(document.querySelector('.album-share-cover')).toHaveAttribute('src', '/mountain-cover');
    expect(screen.getByTestId('album-share-remaining')).toHaveTextContent('2');
    expect(screen.getByTestId('album-share-item-mountain-photo')).toBeInTheDocument();
    expect(screen.queryByTestId('album-share-item-i1')).toBeNull();
    await userEvent.upload(screen.getByTestId('album-share-input'), new File(['new'], 'new.jpg', { type: 'image/jpeg' }));
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(1));
    expect(MockUploadXhr.sent[0].url).toBe(`/api/album-share/${OTHER}/upload`);
  });

  it('drops the old refresh failure while opening another link', async () => {
    let broken = false;
    const next = deferredResponse();
    serveRoutes(() => broken ? new Response(null, { status: 503 }) : jsonResponse(album()), () => next.promise);
    const router = mountRoutes();
    await screen.findByText('Vacanze');
    await userEvent.upload(screen.getByTestId('album-share-input'), new File(['one'], 'one.jpg', { type: 'image/jpeg' }));
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(1));
    broken = true;
    await act(async () => { MockUploadXhr.sent[0].finish(); });
    await screen.findByText(/Non riesco ad aggiornare le foto/);
    await act(async () => { await router.navigate(`/album/${OTHER}`); });
    expect(screen.queryByText(/Non riesco ad aggiornare le foto/)).toBeNull();
    await act(async () => { next.resolve(jsonResponse(otherAlbum())); });
    await screen.findByText('Montagna');
    expect(screen.queryByText(/Non riesco ad aggiornare le foto/)).toBeNull();
  });

  it.each([200, 503])('ignores a late refresh response from the old link (HTTP %s)', async (status) => {
    const old = deferredResponse();
    let refreshing = false;
    const { calls } = serveRoutes(() => refreshing ? old.promise : jsonResponse(album()));
    const router = mountRoutes();
    await screen.findByText('Vacanze');
    await userEvent.upload(screen.getByTestId('album-share-input'), new File(['one'], 'one.jpg', { type: 'image/jpeg' }));
    await waitFor(() => expect(MockUploadXhr.sent).toHaveLength(1));
    refreshing = true;
    await act(async () => { MockUploadXhr.sent[0].finish(); });
    await waitFor(() => expect(calls.filter(call => call.url === `/api/album-share/${TOKEN}`)).toHaveLength(2));
    await act(async () => { await router.navigate(`/album/${OTHER}`); });
    await screen.findByText('Montagna');
    await act(async () => { old.resolve(jsonResponse(album(), status)); });
    expect(screen.getByText('Montagna')).toBeInTheDocument();
    expect(screen.queryByText('Vacanze')).toBeNull();
    expect(screen.queryByText(/Non riesco ad aggiornare le foto/)).toBeNull();
  });
});

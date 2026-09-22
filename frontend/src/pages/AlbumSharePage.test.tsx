import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { AlbumSharePage } from './AlbumSharePage';

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
afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

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
        isVideo: false,
      },
      {
        id: 'i2',
        thumbnailUrl: `/api/album-share/${TOKEN}/media/i2/thumbnail`,
        previewUrl: `/api/album-share/${TOKEN}/media/i2/preview`,
        downloadUrl: `/api/album-share/${TOKEN}/media/i2/download`,
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
  const lightbox = await screen.findByTestId('album-share-lightbox');
  // The grid asked for the SMALL rendition; the viewer asks for the preview.
  expect(lightbox.querySelector('img')).toHaveAttribute(
    'src', `/api/album-share/${TOKEN}/media/i1/preview`);

  const download = screen.getByTestId('album-share-download');
  expect(download).toHaveAttribute('href', `/api/album-share/${TOKEN}/media/i1/download`);
  // The owner left originals off, so the button does not promise one.
  expect(download).toHaveTextContent(/^Scarica$/);
});

it('says when the owner allowed the real file', async () => {
  const user = userEvent.setup();
  serve({ canDownloadOriginal: true });
  mount();

  await user.click(await screen.findByTestId('album-share-item-i1'));
  expect(screen.getByTestId('album-share-download')).toHaveTextContent(/originale/i);
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

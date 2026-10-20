import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { SharePrintPage } from './OwnerPrintPage';

// PRINTING FROM A SHARE: the owner's own studio and options, reached through
// the share's routes, on the owner's printer — which the browser never names.

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const TOKEN = 'a-share-token';

function mount({ printer = { available: true, paper: '10x15', cutsStrips: false }, extra = {} }: {
  printer?: unknown; extra?: Record<string, () => Response>;
} = {}) {
  const mock = installFetchMock({
    [`GET /api/album-share/${TOKEN}`]: () => jsonResponse({
      albumName: 'Vacanze', coverUrl: null, itemCount: 3, canUpload: false, canDownloadOriginal: false,
      uploadsRemaining: null, canPrint: true,
    }),
    [`GET /api/album-share/${TOKEN}/items`]: () => jsonResponse({
      items: ['i1', 'i2', 'v1'].map((id) => ({
        id,
        thumbnailUrl: `/api/album-share/${TOKEN}/media/${id}/thumbnail`,
        previewUrl: `/api/album-share/${TOKEN}/media/${id}/preview`,
        downloadUrl: null, playbackUrl: null, isVideo: id === 'v1',
      })),
      nextCursor: null,
    }),
    [`GET /api/album-share/${TOKEN}/print`]: () => jsonResponse(printer),
    [`POST /api/album-share/${TOKEN}/print/jobs`]: () => jsonResponse(
      { shortCode: 'j1abcdef', state: 'ready', queueAhead: 0 }, 202),
    ...extra,
  });
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[`/album/${TOKEN}/print`]}>
        <Routes><Route path="/album/:token/print" element={<SharePrintPage />} /></Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
  return mock;
}

describe('SharePrintPage', () => {
  it('opens the shared album in the owner\'s studio, with no printer to choose', async () => {
    mount({ printer: { available: true, paper: '10x15', cutsStrips: true } });
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Vacanze');
    expect(screen.queryByTestId('owner-print-printers')).toBeNull();
    // The same formats the owner's paper and printer offer — the strip included.
    expect(screen.getByTestId('party-print-format-photo')).toHaveTextContent('Foto 10×15');
    expect(screen.getByTestId('party-print-format-grid4')).toBeInTheDocument();
    expect(screen.getByTestId('party-print-format-twinStrip4')).toBeInTheDocument();
    for (const back of screen.getAllByText('Torna all’album')) {
      expect(back.closest('a')).toHaveAttribute('href', `/album/${TOKEN}`);
    }

    // Two photographs cannot fill four slots: said, not hidden.
    expect(screen.getByTestId('party-print-format-grid4')).toBeDisabled();
    expect(screen.getByTestId('party-print-format-grid4')).toHaveTextContent('Servono 4 foto, ne hai 2');

    // The album's photographs only — not its video.
    await userEvent.setup().click(screen.getByTestId('party-print-format-photo'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(2);
  });

  it('sends the print to the share, naming no owner, station or printer', async () => {
    const user = userEvent.setup();
    const mock = mount();
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(screen.getAllByRole('button', { name: /Scegli questa foto/ })[0]);
    await user.click(screen.getByRole('button', { name: 'Continua' }));
    await user.click(screen.getByRole('button', { name: 'Continua' }));
    await user.click(screen.getByRole('button', { name: 'Stampa' }));

    await waitFor(() => expect(mock.calls.some((c) => c.method === 'POST')).toBe(true));
    const post = mock.calls.find((c) => c.method === 'POST')!;
    expect(post.url).toContain(`/api/album-share/${TOKEN}/print/jobs`);
    const body = JSON.parse(post.body ?? '{}');
    expect(body).toMatchObject({ expectedPaperSize: '10x15', layout: 'photo', style: 'fullBleed' });
    expect(body.photos).toEqual([{ fileItemId: 'i1', placement: { centerX: 0.5, centerY: 0.5, zoom: 1 } }]);
    expect(body).not.toHaveProperty('printStationId');
    expect(body).not.toHaveProperty('printerDeviceId');
    expect(body).not.toHaveProperty('ownerUserId');
    expect(await screen.findByTestId('owner-print-sent')).toHaveTextContent('Inviata alla stampa');
  });

  it('says so when the owner\'s printer cannot print now', async () => {
    mount({ printer: { available: false, paper: '10x15', cutsStrips: false } });
    expect(await screen.findByTestId('share-print-unavailable')).toHaveTextContent('non può stampare');
  });

  it('says so when printing is no longer allowed', async () => {
    const user = userEvent.setup();
    mount({
      extra: {
        [`POST /api/album-share/${TOKEN}/print/jobs`]: () => jsonResponse({ error: 'print_not_allowed' }, 403),
      },
    });
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(screen.getAllByRole('button', { name: /Scegli questa foto/ })[0]);
    await user.click(screen.getByRole('button', { name: 'Continua' }));
    await user.click(screen.getByRole('button', { name: 'Continua' }));
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('non è più consentita');
  });

  // A visitor's viewer shows the share's own derived preview — never an
  // original, never a download it was not offered — and chooses from there.
  it('opens a photograph in the share-scoped viewer and chooses it there', async () => {
    const user = userEvent.setup();
    const mock = mount();
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(screen.getByRole('button', { name: 'Apri la foto 2' }));
    const viewer = screen.getByTestId('public-viewer');
    expect(viewer.querySelector('img')!.getAttribute('src')).toBe(`/api/album-share/${TOKEN}/media/i2/preview`);
    expect(screen.queryByTestId('public-viewer-download')).toBeNull();
    await user.click(screen.getByTestId('public-viewer-select'));
    await user.click(screen.getByTestId('public-viewer-close'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto|Togli dalla selezione/ })[1])
      .toHaveAttribute('aria-pressed', 'true');
    expect(mock.calls.some((c) => c.method === 'POST')).toBe(false);
  });
});

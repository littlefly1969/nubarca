import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { OwnerPrintPage } from './OwnerPrintPage';

// PRINTING FROM AN ALBUM through the party's own studio: the same steps, with
// the owner's printer, Piena or Cornice, their own line, the date and the mark.

afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const ALBUM = 'A1';

function station(cutsStrips = false, paper: '10x15' | '13x18' = '10x15') {
  return {
    id: 's1', name: 'Sala', enabled: true, desiredState: 'running', status: 'online', lastSeenAt: null,
    agentVersion: '0.5.0', createdAt: '2026-01-01T00:00:00Z', revokedAt: null, queueCount: 0, currentJob: null,
    lastError: null,
    devices: [{
      id: 'd1', displayName: 'DNP DS-RX1HS', manufacturer: 'DNP', model: 'DS-RX1HS', adapterKind: 'cups',
      observedState: 'ready', lastSeenAt: '2026-01-01T00:00:00Z', supportsPhoto10x15: true, cutsStrips,
      loadedPaperSize: paper, papers: ['10x15', '13x18', '20x15'], mediaRemainingPrints: 120,
    }],
  };
}

function items(ids: string[]) {
  return ids.map((id) => ({
    fileItemId: id, name: `${id}.jpg`, mimeType: 'image/jpeg', sizeBytes: 1000,
    addedAt: '2026-01-01T00:00:00Z', thumbnailUrl: null,
  }));
}

function mount(path: string, { stations = [station()], extra = {} }: {
  stations?: unknown[]; extra?: Record<string, () => Response>;
} = {}) {
  const mock = installFetchMock({
    'GET /api/print/stations': () => jsonResponse(stations),
    'GET /api/print/shared-printers': () => jsonResponse([]),
    [`GET /api/albums/${ALBUM}`]: () => jsonResponse({ id: ALBUM, name: 'Vacanze 2026' }),
    [`GET /api/albums/${ALBUM}/items`]: () => jsonResponse(items(['f1', 'f2', 'f3', 'f4', 'f5', 'v1'])
      .map((it) => (it.fileItemId === 'v1' ? { ...it, mimeType: 'video/mp4' } : it))),
    'POST /api/print/photo-jobs': () => jsonResponse(
      { jobId: 'j1', shortCode: 'j1abcdef', state: 'ready', queueAhead: 0, mediaRemainingPrints: 120 }, 202),
    ...extra,
  });
  render(
    <MemoryRouter initialEntries={[path]}>
      <AuthedWrapper>
        <Routes><Route path="/print" element={<OwnerPrintPage />} /></Routes>
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return mock;
}

const next = () => screen.getByRole('button', { name: 'Continua' });

function lastPost(calls: { url: string; method: string; body: string | null }[]) {
  const post = [...calls].reverse().find((c) => c.method === 'POST');
  return post ? JSON.parse(post.body ?? '{}') : null;
}

describe('OwnerPrintPage', () => {
  it('opens on the album, with the printer, and offers what its paper can make — the strip only from a printer that cuts', async () => {
    mount(`/print?album=${ALBUM}`);
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Vacanze 2026');
    expect(screen.getByTestId('owner-print-printer-d1')).toHaveTextContent('DNP DS-RX1HS');
    expect(screen.getByTestId('party-print-format-photo')).toHaveTextContent('Foto 10×15');
    expect(screen.getByTestId('party-print-format-grid4')).toBeInTheDocument();
    expect(screen.queryByTestId('party-print-format-twinStrip4')).toBeNull();

    // The album's photographs only — not its video.
    await userEvent.setup().click(screen.getByTestId('party-print-format-grid4'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto/ })).toHaveLength(5);
  });

  it('offers both strips when the printer cuts them', async () => {
    mount(`/print?album=${ALBUM}`, { stations: [station(true)] });
    expect(await screen.findByTestId('party-print-format-twinStrip4')).toHaveTextContent('Due strisce diverse');
    expect(screen.getByTestId('party-print-format-twinStrip4-4')).toHaveTextContent('Due strisce uguali');
  });

  it('frames one photograph in Cornice, with the owner\'s line, the date and the mark, and sends exactly that', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f2`, {
      extra: { 'GET /api/print/photo-jobs/date': () => jsonResponse({ date: '2019-07-01', source: 'embedded' }) },
    });
    await user.click(await screen.findByTestId('party-print-format-photo'));
    // The photograph selected in the album is already chosen.
    expect(screen.getByRole('button', { name: /Togli dalla selezione/ })).toBeInTheDocument();
    await user.click(next());
    await user.click(next());

    // Piena first, as a direct print always was; Cornice gives the band.
    expect(screen.getByRole('radio', { name: 'Piena' })).toBeChecked();
    expect(screen.queryByTestId('owner-print-caption')).toBeNull();
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    await user.type(screen.getByTestId('owner-print-caption'), 'Estate al lago');
    await user.click(screen.getByTestId('owner-print-brand'));
    await user.click(screen.getByTestId('owner-print-date'));

    const sheet = screen.getByTestId('party-print-sheet');
    expect(sheet).toHaveTextContent('Estate al lago');
    expect(await screen.findByTestId('print-sheet-mark')).toHaveTextContent('01/07/2019');
    expect(sheet.querySelector('.party-print-sheet-mark')).not.toBeNull();

    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const body = lastPost(mock.calls);
    expect(body).toMatchObject({
      printStationId: 's1', printerDeviceId: 'd1', expectedPaperSize: '10x15',
      layout: 'photo', style: 'framed', caption: 'Estate al lago', brand: true,
      includeDate: true, dateLocale: 'it',
    });
    expect(body.photos).toEqual([{ fileItemId: 'f2', placement: { centerX: 0.5, centerY: 0.5, zoom: 1 } }]);
    expect(['portrait', 'landscape']).toContain(body.orientation);
    expect(await screen.findByTestId('owner-print-sent')).toHaveTextContent('Inviata alla stampa');
  });

  it('sends four photographs in the order they were chosen, framed, with no orientation of their own', async () => {
    const user = userEvent.setup();
    const mock = mount(`/print?album=${ALBUM}&files=f3,f1,f4,f2`);
    await user.click(await screen.findByTestId('party-print-format-grid4'));
    await user.click(next());
    await user.click(next());
    for (let i = 0; i < 4; i += 1) await user.click(next());

    // Four photographs are always framed: no Piena to choose, the line is offered.
    expect(screen.queryByRole('radio', { name: 'Piena' })).toBeNull();
    expect(screen.getByTestId('owner-print-caption')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    await waitFor(() => expect(lastPost(mock.calls)).not.toBeNull());
    const body = lastPost(mock.calls);
    expect(body.layout).toBe('grid4');
    expect(body.style).toBe('framed');
    expect(body).not.toHaveProperty('orientation');
    expect(body).not.toHaveProperty('caption');
    expect(body.photos.map((p: { fileItemId: string }) => p.fileItemId)).toEqual(['f3', 'f1', 'f4', 'f2']);
  });

  it('will not send a line the band cannot hold', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1`);
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByRole('radio', { name: 'Cornice' }));
    await user.type(screen.getByTestId('owner-print-caption'), 'a'.repeat(41));
    expect(screen.getByText(/Al massimo 40 caratteri/)).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Stampa' })).toBeDisabled();
  });

  it('says so when the loan has no sheets left', async () => {
    const user = userEvent.setup();
    mount(`/print?album=${ALBUM}&files=f1`, {
      extra: { 'POST /api/print/photo-jobs': () => jsonResponse({ error: 'share_exhausted' }, 409) },
    });
    await user.click(await screen.findByTestId('party-print-format-photo'));
    await user.click(next());
    await user.click(next());
    await user.click(screen.getByRole('button', { name: 'Stampa' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('I fogli della condivisione sono finiti');
  });

  it('prints the photographs selected in the library, and only those', async () => {
    const user = userEvent.setup();
    mount('/print?files=f9,f8');
    expect(await screen.findByRole('heading', { level: 1 })).toHaveTextContent('Stampa foto');
    await user.click(screen.getByTestId('party-print-format-photo'));
    expect(screen.getAllByRole('button', { name: /Scegli questa foto|Togli dalla selezione/ })).toHaveLength(2);
    expect(screen.getByText('Torna alla libreria')).toBeInTheDocument();
  });

  it('says there is no printer to use rather than offering a studio that cannot print', async () => {
    mount(`/print?album=${ALBUM}`, { stations: [] });
    expect(await screen.findByTestId('owner-print-empty')).toHaveTextContent('Nessuna stampante disponibile');
    expect(screen.queryByTestId('party-print-format-photo')).toBeNull();
  });
});

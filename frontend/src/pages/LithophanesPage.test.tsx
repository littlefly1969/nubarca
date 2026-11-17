import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router';
import type { LithophaneSummary } from '@nubarca/api-client';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { LithophanesPage } from './LithophanesPage';
import { DEFAULT_LITHOPHANE_SETTINGS } from '../lithophanes/lithophaneModel';

vi.mock('../lithophanes/LithophaneSphere', () => ({
  LithophaneSphere: () => <div role="img" aria-label="Sfera di prova" />,
}));

const photo = { id: 'photo-1' };
const waiting = { fileItemId: 'photo-1', name: 'Famiglia.png', thumbnailUrl: '/api/files/photo-1/thumbnail?size=small', addedAt: '2026-11-14T12:00:00Z' };
const queue = [waiting];
const preview = { imageBase64: 'test', columns: 100, rows: 50, topRows: 25, bottomAngle: 2.9, triangleCount: 20000, sizeBytes: 1000084 };
const project: LithophaneSummary = {
  id: 'project-1', name: 'Famiglia.png', fileItemId: 'photo-1', settings: DEFAULT_LITHOPHANE_SETTINGS,
  status: 'queued', completedRows: 0, totalRows: 50, triangleCount: 20000, sizeBytes: 1000084,
  createdAt: '2026-11-13T12:00:00Z', downloadReady: false,
};

beforeEach(() => { vi.spyOn(window, 'confirm').mockReturnValue(true); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const mount = () => render(<MemoryRouter><AuthedWrapper><LithophanesPage /></AuthedWrapper></MemoryRouter>);
async function choose() { fireEvent.click(await screen.findByRole('button', { name: 'Famiglia.png' })); }

describe('Laboratory lithophanes', () => {
  it('selects an existing photo, previews the exact settings and queues a generation', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse(preview),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    fireEvent.change(screen.getByLabelText('Diametro esterno (mm)'), { target: { value: '90' } });
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    expect(await screen.findByRole('img', { name: 'Sfera di prova' })).toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    expect(await screen.findByText('In coda')).toBeInTheDocument();
    const request = mock.calls.find((c) => c.url === '/api/lithophanes' && c.method === 'POST')!;
    expect(JSON.parse(request.body!)).toMatchObject({ fileItemId: photo.id, settings: { diameterMm: 90, backgroundThicknessMm: 0.8, baseCollar: true } });
    // The server ended the photo's wait when it accepted the generation.
    expect(screen.queryByRole('button', { name: 'Famiglia.png' })).not.toBeInTheDocument();
    expect(JSON.parse(request.body!).requestId).toMatch(/^[a-f0-9-]{36}$/);
    expect(mock.calls.some((c) => /\/content|\/download/.test(c.url))).toBe(false);
  });

  it('can disable the default collar and uses that choice for preview and generation', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse(preview),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    const collar = screen.getByRole('checkbox', { name: 'Base verticale proporzionata al foro' });
    expect(collar).toBeChecked();
    fireEvent.click(collar);
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText('In coda');
    for (const url of ['/api/lithophanes/preview', '/api/lithophanes']) {
      const request = mock.calls.find((c) => c.url === url && c.method === 'POST')!;
      expect(JSON.parse(request.body!).settings.baseCollar).toBe(false);
    }
  });

  it('adds words to a photo in the chosen face and position', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse(preview),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    fireEvent.change(screen.getByLabelText('Testo (una o due righe)'), { target: { value: 'Buon\nNatale' } });
    fireEvent.click(screen.getByLabelText('Corsivo'));
    fireEvent.change(screen.getByLabelText('Posizione'), { target: { value: 'above' } });
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText('In coda');
    const previewRequest = mock.calls.find((c) => c.url === '/api/lithophanes/preview')!;
    const request = mock.calls.find((c) => c.url === '/api/lithophanes' && c.method === 'POST')!;
    const words = { text: 'Buon\nNatale', textFont: 'script', textHeightMm: 8, textPosition: 'above' };
    expect(JSON.parse(previewRequest.body!).settings).toMatchObject(words);
    expect(JSON.parse(request.body!)).toMatchObject({ fileItemId: photo.id, settings: words });
  });

  it('sends no text fields at all when there are no words', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    fireEvent.click(screen.getByLabelText('Elegante'));
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText('In coda');
    const settings = JSON.parse(mock.calls.find((c) => c.url === '/api/lithophanes' && c.method === 'POST')!.body!).settings;
    for (const key of ['text', 'textFont', 'textHeightMm', 'textPosition']) expect(settings).not.toHaveProperty(key);
  });

  it('shows the parameters and the text at once, even with no photo waiting', async () => {
    installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse([]),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
    });
    mount();
    expect(await screen.findByText(/Nessuna foto in attesa/)).toBeInTheDocument();
    expect(screen.getByLabelText('Diametro esterno (mm)')).toBeInTheDocument();
    expect(screen.getByLabelText('Testo (una o due righe)')).toBeInTheDocument();
    expect(screen.getByText(/Nessuna foto scelta/)).toBeInTheDocument();
  });

  it('makes a lithophane of words only, without a photo', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse([]),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse(preview),
      'POST /api/lithophanes': () => jsonResponse({ ...project, fileItemId: null, name: 'Auguri' }, 201),
    });
    mount();
    fireEvent.click(await screen.findByRole('button', { name: 'Crea una litofania con solo una scritta' }));
    // Nothing to place or tone, and no position to choose.
    expect(screen.queryByLabelText('Ingrandimento')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Posizione')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Genera STL' })).toBeDisabled();
    expect(screen.getByText(/senza foto la scritta è tutta la litofania/)).toBeInTheDocument();
    fireEvent.change(screen.getByLabelText('Testo (una o due righe)'), { target: { value: 'Auguri' } });
    fireEvent.click(screen.getByLabelText('A mano'));
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText('In coda');
    const previewRequest = JSON.parse(mock.calls.find((c) => c.url === '/api/lithophanes/preview')!.body!);
    const request = JSON.parse(mock.calls.find((c) => c.url === '/api/lithophanes' && c.method === 'POST')!.body!);
    expect(previewRequest.fileItemId).toBeNull();
    expect(request).toMatchObject({ fileItemId: null, settings: { text: 'Auguri', textFont: 'hand' } });
  });

  it('explains a too-detailed large sphere and applies the suggested surface step', async () => {
    installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
    });
    mount(); await choose();
    fireEvent.change(screen.getByLabelText('Diametro esterno (mm)'), { target: { value: '120' } });
    expect(screen.getByRole('button', { name: 'Genera STL' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('troppo fine');
    fireEvent.click(screen.getByRole('button', { name: /^Usa .* mm$/ }));
    const step = Number((screen.getByLabelText('Passo della superficie (mm)') as HTMLInputElement).value);
    expect(step).toBeGreaterThan(0.3);
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Genera STL' })).toBeEnabled();
  });

  it('says when the zoom is below what changes the photo and offers the minimum', async () => {
    installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse({ ...preview, minimumZoom: 0.6234 }),
    });
    mount(); await choose();
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    expect(screen.queryByText(/lo zoom non cambia più nulla/)).not.toBeInTheDocument(); // default zoom 1
    fireEvent.change(screen.getByLabelText('Ingrandimento'), { target: { value: '0.3' } });
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    expect(await screen.findByText(/Sotto 0,63 lo zoom non cambia più nulla/)).toBeInTheDocument();
    // The fix lands on the field's 0.05 step, which the browser would otherwise refuse.
    fireEvent.click(screen.getByRole('button', { name: 'Usa 0,65' }));
    expect((screen.getByLabelText('Ingrandimento') as HTMLInputElement).value).toBe('0.65');
    expect((screen.getByLabelText('Ingrandimento') as HTMLInputElement).validity.valid).toBe(true);
    expect(screen.queryByText(/lo zoom non cambia più nulla/)).not.toBeInTheDocument();
  });

  it('preserves the idempotency key after a lost creation response', async () => {
    let attempt = 0;
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes': () => ++attempt === 1 ? errorResponse(503) : jsonResponse(project, 201),
    });
    mount(); await choose();
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText('Operazione non riuscita. Controlla i parametri e riprova.');
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText('In coda');
    const requests = mock.calls.filter((c) => c.url === '/api/lithophanes' && c.method === 'POST');
    expect(JSON.parse(requests[0].body!).requestId).toEqual(JSON.parse(requests[1].body!).requestId);
  });

  it('rejects impossible geometry locally and invalidates an outdated preview', async () => {
    installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse(preview),
    });
    mount(); await choose();
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.change(screen.getByLabelText('Apertura inferiore per il LED (mm)'), { target: { value: '79' } });
    expect(screen.queryByRole('img', { name: 'Sfera di prova' })).not.toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Genera STL' })).toBeDisabled();
    expect(screen.getByRole('alert')).toHaveTextContent('Controlla i valori');
  });

  it('restores history, offers downloads only when ready and removes a deleted project', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [{ ...project, status: 'ready', downloadReady: true }], nextCursor: null }),
      'DELETE /api/lithophanes/project-1': () => new Response(null, { status: 204 }),
    });
    mount();
    expect(await screen.findByRole('link', { name: 'Scarica STL' })).toHaveAttribute('href', '/api/lithophanes/project-1/download');
    fireEvent.click(screen.getByRole('button', { name: 'Elimina' }));
    await waitFor(() => expect(screen.queryByRole('link', { name: 'Scarica STL' })).not.toBeInTheDocument());
    expect(mock.calls.some((c) => c.method === 'DELETE')).toBe(true);
  });

  it('cancels an active run and presents the pending cancellation', async () => {
    installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [project], nextCursor: null }),
      'POST /api/lithophanes/project-1/cancel': () => new Response(null, { status: 204 }),
    });
    mount();
    expect(screen.queryByRole('link', { name: 'Scarica STL' })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole('button', { name: 'Annulla generazione' }));
    await screen.findByText('Annullamento in corso');
    expect(screen.queryByRole('button', { name: 'Annulla generazione' })).not.toBeInTheDocument();
  });

  it('works only on the photos sent from the library or an album, and removes one on request', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'GET /api/lithophanes/queue': () => jsonResponse([waiting, { ...waiting, fileItemId: 'photo-2', name: 'Montagna.jpg' }]),
      'DELETE /api/lithophanes/queue/photo-2': () => new Response(null, { status: 204 }),
    });
    mount();
    expect(await screen.findByRole('button', { name: 'Montagna.jpg' })).toBeInTheDocument();
    expect(screen.queryByRole('searchbox')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Togli Montagna.jpg dalle foto da trasformare' }));
    await waitFor(() => expect(screen.queryByRole('button', { name: 'Montagna.jpg' })).not.toBeInTheDocument());
    expect(screen.getByRole('button', { name: 'Famiglia.png' })).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'DELETE' && c.url === '/api/lithophanes/queue/photo-2')).toBe(true);
    expect(mock.calls.some((c) => c.url.startsWith('/api/media'))).toBe(false);
  });

  it('explains where photos come from when none is waiting', async () => {
    installFetchMock({
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'GET /api/lithophanes/queue': () => jsonResponse([]),
    });
    mount();
    expect(await screen.findByText(/Nessuna foto in attesa/)).toBeInTheDocument();
    expect(screen.getByRole('link', { name: 'Apri la Libreria' })).toHaveAttribute('href', '/media');
    expect(screen.getByRole('link', { name: 'Apri gli album' })).toHaveAttribute('href', '/albums');
  });

  it('sets the background wall separately and checks its limits', async () => {
    installFetchMock({
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
    });
    mount(); await choose();
    const field = screen.getByLabelText('Spessore dello sfondo (mm)') as HTMLInputElement;
    expect(field.value).toBe('0.8');
    fireEvent.change(field, { target: { value: '2.5' } });
    expect(screen.getByRole('button', { name: 'Genera STL' })).toBeDisabled();
    fireEvent.change(field, { target: { value: '1.6' } });
    expect(screen.getByRole('button', { name: 'Genera STL' })).toBeEnabled();
  });
});

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
function openSource() { fireEvent.click(screen.getByRole('button', { name: /^Foto da trasformare/ })); }
function openHistory() { fireEvent.click(screen.getByRole('button', { name: /^Le tue litofanie/ })); }
async function choose() { openSource(); fireEvent.click(await screen.findByRole('button', { name: 'Famiglia.png' })); }

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
    expect(await screen.findByText(/In coda/, { selector: '.lithophane-latest' })).toBeInTheDocument();
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
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
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
    fireEvent.change(screen.getByTestId('lithophane-text-latitude'), { target: { value: '0' } });
    fireEvent.click(screen.getByRole('button', { name: /Altezza dal centro.*−1°/ }));
    expect(screen.getByTestId('lithophane-text-latitude')).toHaveValue(-1);
    fireEvent.change(screen.getByTestId('lithophane-text-latitude'), { target: { value: '25' } });
    fireEvent.change(screen.getByTestId('lithophane-text-longitude'), { target: { value: '-30' } });
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
    const previewRequest = mock.calls.find((c) => c.url === '/api/lithophanes/preview')!;
    const request = mock.calls.find((c) => c.url === '/api/lithophanes' && c.method === 'POST')!;
    const words = { text: 'Buon\nNatale', textFont: 'script', textHeightMm: 8, textLatitudeDegrees: 25, textLongitudeDegrees: -30 };
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
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
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
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
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

  it('chooses a crop with a preview and scales it independently on the sphere', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes/preview': () => jsonResponse(preview),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    const frame = screen.getByTestId('lithophane-crop');
    const image = frame.querySelector('img')!;
    expect(image).toHaveAttribute('src', '/api/files/photo-1/preview');
    Object.defineProperty(image, 'naturalWidth', { value: 1200 });
    Object.defineProperty(image, 'naturalHeight', { value: 600 });
    fireEvent.load(image);
    fireEvent.change(screen.getByLabelText('Proporzioni del ritaglio'), { target: { value: '1' } });
    fireEvent.change(screen.getByLabelText('Zoom del ritaglio'), { target: { value: '2' } });
    fireEvent.keyDown(frame, { key: 'ArrowRight' });
    const cropStyle = image.getAttribute('style');
    fireEvent.change(screen.getByLabelText('2. Dimensione sulla sfera'), { target: { value: '60' } });
    expect(image.getAttribute('style')).toBe(cropStyle);
    expect(screen.queryByLabelText('Larghezza della foto sulla sfera (°)')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Altezza della foto sulla sfera (°)')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
    const sent = mock.calls.filter((c) => c.method === 'POST').map((c) => JSON.parse(c.body!).settings);
    expect(sent).toHaveLength(2);
    expect(sent[0]).toEqual(sent[1]);
    expect(sent[0]).toMatchObject({ frameAspect: 1, pictureScale: 0.6, centerX: 0.52, centerY: 0.5, zoom: 2, externalRelief: true });
    expect(mock.calls.some((c) => /content|download/.test(c.url))).toBe(false);
  });

  it('offers a short internal joint and inward brims on both hanging parts', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    fireEvent.change(screen.getByLabelText('Come userai la sfera'), { target: { value: 'hanging' } });
    expect(screen.getByLabelText('Brim interno rimovibile su sfera e calotta')).toBeChecked();
    expect(screen.queryByLabelText('Ancoraggio temporaneo da rimuovere')).not.toBeInTheDocument();
    expect(screen.queryByLabelText('Base verticale proporzionata al foro')).not.toBeInTheDocument();
    expect(screen.queryByRole('button', { name: /parziale/i })).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
    expect(JSON.parse(mock.calls.find((c) => c.method === 'POST')!.body!).settings)
      .toMatchObject({ hanging: true, externalRelief: true, printAnchor: false, internalFit: true, printBrim: true, openingMm: 30 });
  });

  it('can omit both built-in brims and resets the internal joint when returning to standing mode', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse(queue),
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount(); await choose();
    const mode = screen.getByLabelText('Come userai la sfera');
    fireEvent.change(mode, { target: { value: 'hanging' } });
    fireEvent.click(screen.getByLabelText('Brim interno rimovibile su sfera e calotta'));
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
    expect(JSON.parse(mock.calls.find((c) => c.method === 'POST')!.body!).settings)
      .toMatchObject({ internalFit: true, printBrim: false, printAnchor: false });
    fireEvent.change(mode, { target: { value: 'standing' } });
    expect(screen.queryByLabelText('Brim interno rimovibile su sfera e calotta')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await waitFor(() => expect(mock.calls.filter((c) => c.method === 'POST')).toHaveLength(2));
    expect(JSON.parse(mock.calls.filter((c) => c.method === 'POST')[1].body!).settings)
      .toMatchObject({ internalFit: false, printBrim: false, hanging: false });
  });

  it('offers a complete cap only for a ready hanging project', async () => {
    installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse([]),
      'GET /api/lithophanes': () => jsonResponse({ items: [{ ...project, settings: { ...project.settings, hanging: true }, status: 'ready', downloadReady: true }], nextCursor: null }),
    });
    mount();
    openHistory(); fireEvent.click(await screen.findByText('Famiglia.png'));
    expect(await screen.findByRole('link', { name: 'Scarica calotta STL' })).toHaveAttribute('href', '/api/lithophanes/project-1/cap');
  });

  it('keeps a saved legacy preview and lets its next generation opt into the internal joint', async () => {
    const saved = { ...project, status: 'ready', downloadReady: true,
      settings: { ...project.settings, hanging: true, printAnchor: true, openingMm: 30 } };
    const mock = installFetchMock({
      'GET /api/lithophanes/queue': () => jsonResponse([]),
      'GET /api/lithophanes': () => jsonResponse({ items: [saved], nextCursor: null }),
      'GET /api/lithophanes/project-1/preview': () => jsonResponse({ ...preview, hanging: true }),
      'POST /api/lithophanes': () => jsonResponse(project, 201),
    });
    mount();
    openHistory(); fireEvent.click(await screen.findByText('Famiglia.png'));
    fireEvent.click(await screen.findByRole('button', { name: 'Vedi anteprima' }));
    await screen.findByRole('img', { name: 'Sfera di prova' });
    fireEvent.click(screen.getByRole('button', { name: 'Riutilizza configurazione' }));
    const fit = await screen.findByLabelText('Innesto interno da 2 mm, senza piede svasato');
    expect(fit).not.toBeChecked();
    expect(screen.getByLabelText('Ancoraggio temporaneo da rimuovere')).toBeChecked();
    fireEvent.click(fit);
    expect(screen.getByLabelText('Brim interno rimovibile su sfera e calotta')).toBeChecked();
    expect(screen.queryByLabelText('Ancoraggio temporaneo da rimuovere')).not.toBeInTheDocument();
    fireEvent.click(screen.getByRole('button', { name: 'Genera STL' }));
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'POST')).toBe(true));
    expect(JSON.parse(mock.calls.find((c) => c.method === 'POST')!.body!).settings)
      .toMatchObject({ internalFit: true, printBrim: true, printAnchor: false, openingMm: 30 });
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
    await screen.findByText(/In coda/, { selector: '.lithophane-latest' });
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
    expect(screen.getByRole('img', { name: 'Sfera di prova' })).toBeInTheDocument();
    expect(screen.getByText('Anteprima da aggiornare')).toBeInTheDocument();
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
    openHistory(); fireEvent.click(await screen.findByText('Famiglia.png'));
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
    openHistory(); fireEvent.click(await screen.findByRole('button', { name: 'Annulla generazione' }));
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
    openSource();
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
    openSource();
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

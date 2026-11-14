import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import type { LithophaneSummary } from '@nubarca/api-client';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { LithophanesPage } from './LithophanesPage';
import { DEFAULT_LITHOPHANE_SETTINGS } from '../lithophanes/lithophaneModel';

vi.mock('../lithophanes/LithophaneSphere', () => ({
  LithophaneSphere: () => <div role="img" aria-label="Sfera di prova" />,
}));

const photo = { id: 'photo-1', kind: 'image', name: 'Famiglia.png', displayName: 'Famiglia', thumbnailUrl: '/thumb.jpg' };
const media = { items: [photo], nextCursor: null, hasMore: false, total: 1 };
const preview = { imageBase64: 'test', columns: 100, rows: 50, topRows: 25, bottomAngle: 2.9, triangleCount: 20000, sizeBytes: 1000084 };
const project: LithophaneSummary = {
  id: 'project-1', name: 'Famiglia.png', fileItemId: 'photo-1', settings: DEFAULT_LITHOPHANE_SETTINGS,
  status: 'queued', completedRows: 0, totalRows: 50, triangleCount: 20000, sizeBytes: 1000084,
  createdAt: '2026-11-13T12:00:00Z', downloadReady: false,
};

beforeEach(() => { vi.spyOn(window, 'confirm').mockReturnValue(true); });
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });
const mount = () => render(<AuthedWrapper><LithophanesPage /></AuthedWrapper>);
async function choose() { fireEvent.click(await screen.findByRole('button', { name: 'Famiglia' })); }

describe('Laboratory lithophanes', () => {
  it('selects an existing photo, previews the exact settings and queues a generation', async () => {
    const mock = installFetchMock({
      'GET /api/media': () => jsonResponse(media),
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
    expect(JSON.parse(request.body!)).toMatchObject({ fileItemId: photo.id, settings: { diameterMm: 90 } });
    expect(JSON.parse(request.body!).requestId).toMatch(/^[a-f0-9-]{36}$/);
    expect(mock.calls.some((c) => /\/content|\/download/.test(c.url))).toBe(false);
  });

  it('explains a too-detailed large sphere and applies the suggested surface step', async () => {
    installFetchMock({
      'GET /api/media': () => jsonResponse(media),
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

  it('preserves the idempotency key after a lost creation response', async () => {
    let attempt = 0;
    const mock = installFetchMock({
      'GET /api/media': () => jsonResponse(media),
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
      'GET /api/media': () => jsonResponse(media),
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
      'GET /api/media': () => jsonResponse(media),
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
      'GET /api/media': () => jsonResponse(media),
      'GET /api/lithophanes': () => jsonResponse({ items: [project], nextCursor: null }),
      'POST /api/lithophanes/project-1/cancel': () => new Response(null, { status: 204 }),
    });
    mount();
    expect(screen.queryByRole('link', { name: 'Scarica STL' })).not.toBeInTheDocument();
    fireEvent.click(await screen.findByRole('button', { name: 'Annulla generazione' }));
    await screen.findByText('Annullamento in corso');
    expect(screen.queryByRole('button', { name: 'Annulla generazione' })).not.toBeInTheDocument();
  });

  it('loads further photo pages through the existing cursor', async () => {
    const mock = installFetchMock({
      'GET /api/lithophanes': () => jsonResponse({ items: [], nextCursor: null }),
      'GET /api/media': ({ url }) => jsonResponse(url.includes('cursor=next')
        ? { ...media, items: [{ ...photo, id: 'photo-2', displayName: 'Montagna' }] }
        : { ...media, nextCursor: 'next' }),
    });
    mount(); fireEvent.click(await screen.findByRole('button', { name: 'Altre foto' }));
    await screen.findByRole('button', { name: 'Montagna' });
    expect(screen.getByRole('button', { name: 'Famiglia' })).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.includes('cursor=next'))).toBe(true);
  });
});

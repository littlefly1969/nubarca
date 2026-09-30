import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PrintStationsPanel } from './PrintStationsPanel';
import { AuthedWrapper, emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

const station = {
  id: '11111111-1111-1111-1111-111111111111', name: 'Studio', enabled: true,
  desiredState: 'running', status: 'online', lastSeenAt: '2026-09-01T12:00:00Z',
  agentVersion: '0.1.0', createdAt: '2026-09-01T10:00:00Z', revokedAt: null,
  devices: [{ id: '22222222-2222-2222-2222-222222222222', displayName: 'DNP DS620',
    manufacturer: 'DNP', model: 'DS620', adapterKind: 'windows-spooler', observedState: 'ready',
    lastSeenAt: '2026-09-01T12:00:00Z', supportsPhoto10x15: true }], queueCount: 1,
  currentJob: { id: 'j1', shortCode: 'abc12345', kind: 'diagnostic', format: '10x15',
    state: 'ready', createdAt: '2026-09-01T12:00:00Z', failureCode: null }, lastError: null,
};

/** Every test starts with nothing lent to the reader, unless it says otherwise. */
function mockApi(handlers: Parameters<typeof installFetchMock>[0]) {
  return installFetchMock({ 'GET /api/print/shared-printers': () => jsonResponse([]), ...handlers });
}

function view() { return <AuthedWrapper><PrintStationsPanel /></AuthedWrapper>; }

describe('PrintStationsPanel', () => {
  it('renders the empty state', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([]) });
    render(view());
    expect(await screen.findByTestId('print-empty')).toBeInTheDocument();
  });

  it('renders online status, printer, queue and current job', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([station]) });
    render(view());
    expect(await screen.findByText('Studio')).toBeInTheDocument();
    expect(screen.getByText('Online')).toBeInTheDocument();
    expect(screen.getByText('DNP DS620')).toBeInTheDocument();
    expect(screen.getByText(/abc12345/)).toBeInTheDocument();
    // A DS620 without a cutting queue has no twin strips at all.
    expect(screen.getByTestId('print-strip-cut')).toHaveTextContent('Non disponibili: serve il taglio della stampante');
  });

  it('adjusts the printer colours and saves them for the next sheets', async () => {
    const user = userEvent.setup();
    const device = station.devices[0];
    const url = `/api/print/stations/${station.id}/devices/${device.id}/calibration`;
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([station]),
      [`PUT ${url}`]: () => jsonResponse({ ...device, calibration: { brightness: 1, contrast: 1, gamma: 1.2, saturation: 1 } }),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Regola colori' }));

    const midtones = screen.getByLabelText('Toni medi');
    expect(midtones).toHaveValue('1');
    fireEvent.change(midtones, { target: { value: '1.2' } });
    expect(screen.getByText('+20%')).toBeInTheDocument();

    await user.click(screen.getByRole('button', { name: 'Salva' }));
    expect(await screen.findByText(/Salvato\. Premi «Stampa pagina test»/)).toBeInTheDocument();
    const put = mock.calls.find((c) => c.method === 'PUT' && c.url === url);
    expect(JSON.parse(put!.body!)).toEqual({ brightness: 1, contrast: 1, gamma: 1.2, saturation: 1 });

    // Back to neutral in one press, saved the same way.
    await user.click(screen.getByRole('button', { name: 'Valori neutri' }));
    expect(screen.getByLabelText('Toni medi')).toHaveValue('1');
  });

  it('says when the printer cuts strips itself', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([
      { ...station, devices: [{ ...station.devices[0], displayName: 'DNP DS-RX1HS', cutsStrips: true }] },
    ]) });
    render(view());
    expect(await screen.findByTestId('print-strip-cut'))
      .toHaveTextContent('Tagliate dalla stampante (2×6)');
  });

  it('renders offline status and the bounded last error', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([
      { ...station, status: 'offline', lastError: 'printer_offline' },
    ]) });
    render(view());
    expect(await screen.findByText('Offline')).toBeInTheDocument();
    expect(screen.getByText('printer_offline')).toBeInTheDocument();
  });

  it('creates a station and exposes the one-shot enrollment command', async () => {
    const user = userEvent.setup();
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([]),
      'POST /api/print/stations': () => jsonResponse({ id: station.id, name: 'Sala',
        enrollmentToken: 'one-shot', enrollmentExpiresAt: '2026-09-01T12:10:00Z' }, 201),
    });
    render(view());
    await user.type(await screen.findByLabelText('Nome stazione'), 'Sala');
    await user.click(screen.getByRole('button', { name: 'Crea stazione' }));
    expect(await screen.findByTestId('print-enrollment')).toHaveTextContent('one-shot');
    expect(mock.calls.some((x) => x.method === 'POST' && x.url.includes('/api/print/stations'))).toBe(true);
  });

  it('pauses a running station', async () => {
    const user = userEvent.setup();
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([station]),
      [`PUT /api/print/stations/${station.id}/desired-state`]: () => emptyResponse(204),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Pausa' }));
    await waitFor(() => expect(mock.calls.some((x) => x.method === 'PUT')).toBe(true));
  });

  it('resumes a paused station', async () => {
    const user = userEvent.setup();
    const paused = { ...station, desiredState: 'paused' };
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([paused]),
      [`PUT /api/print/stations/${station.id}/desired-state`]: () => emptyResponse(204),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Riprendi' }));
    await waitFor(() => expect(mock.calls.some((x) => x.method === 'PUT'
      && x.body?.includes('running'))).toBe(true));
  });

  it('queues a test print for the detected printer', async () => {
    const user = userEvent.setup();
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([station]),
      [`POST /api/print/stations/${station.id}/test-jobs`]: () => jsonResponse(station.currentJob, 202),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Stampa pagina test' }));
    await waitFor(() => expect(mock.calls.some((x) => x.method === 'POST'
      && x.url.includes('/test-jobs'))).toBe(true));
  });

  it('does not offer a test print to an offline or incompatible device', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([
      { ...station, devices: [{ ...station.devices[0], observedState: 'offline' }] },
    ]) });
    render(view());
    expect(await screen.findByRole('button', { name: 'Stampa pagina test' })).toBeDisabled();
  });

  it('records which paper is in the printer, and warns when the agent cannot print it', async () => {
    const user = userEvent.setup();
    const device = { ...station.devices[0], loadedPaperSize: '10x15', papers: ['10x15', '20x15'] };
    const url = `/api/print/stations/${station.id}/devices/${device.id}/paper`;
    let current = device;
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([{ ...station, devices: [current] }]),
      [`PUT ${url}`]: () => {
        current = { ...device, loadedPaperSize: '13x18' };
        return jsonResponse(current);
      },
    });
    render(view());
    const select = await screen.findByLabelText('Carta caricata');
    expect(select).toHaveValue('10x15');
    expect(screen.queryByTestId('print-paper-unsupported')).not.toBeInTheDocument();

    await user.selectOptions(select, '13x18');
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'PUT' && c.url === url)).toBe(true));
    expect(JSON.parse(mock.calls.find((c) => c.method === 'PUT')!.body!)).toEqual({ paperSize: '13x18' });
    // 13x18 is in, but this agent reports only 10x15 and 20x15.
    expect(await screen.findByTestId('print-paper-unsupported'))
      .toHaveTextContent('Il Print Agent non riporta la carta 13×18');
  });

  it('changes who set the paper, and says when', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([{ ...station, devices: [{
      ...station.devices[0], loadedPaperSize: '20x15', papers: ['10x15', '20x15'],
      loadedPaperChangedBy: 'Mario', loadedPaperChangedAt: '2026-09-02T18:30:00Z' }] }]) });
    render(view());
    expect(await screen.findByTestId('print-paper-changed')).toHaveTextContent(/Cambiata da Mario il/);
  });

  // --- The queue ------------------------------------------------------------

  it('lists the queue with its sender, what waits for paper, and cancels a sheet', async () => {
    const user = userEvent.setup();
    const waiting = { id: 'j2', shortCode: 'def67890', kind: 'party-grid4', format: '13x18', state: 'ready',
      createdAt: '2026-09-01T12:01:00Z', failureCode: null, waitingForPaper: '13x18', ownerName: 'Mario' };
    const printing = { id: 'j3', shortCode: 'aaa11111', kind: 'party-strip4', format: '2x6x2', state: 'submitted',
      createdAt: '2026-09-01T12:00:30Z', failureCode: null, waitingForPaper: null, ownerName: null };
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([{ ...station, queue: [printing, waiting] }]),
      'POST /api/print/jobs/j2/cancel': () => emptyResponse(204),
    });
    render(view());
    const rows = await screen.findAllByTestId('print-queue-row');
    expect(rows).toHaveLength(2);
    // Already at the printer: nothing to take back.
    expect(rows[0]).toHaveTextContent('Strisce · 2×6');
    expect(rows[0]).toHaveTextContent('Inviata alla stampante');
    expect(within(rows[0]).queryByRole('button', { name: 'Annulla' })).not.toBeInTheDocument();
    // Made for another paper: it says which, and whose it is.
    expect(rows[1]).toHaveTextContent('4 foto · 13×18');
    expect(rows[1]).toHaveTextContent('da Mario');
    expect(rows[1]).toHaveTextContent('In attesa della carta 13×18');

    await user.click(within(rows[1]).getByRole('button', { name: 'Annulla' }));
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'POST'
      && c.url === '/api/print/jobs/j2/cancel')).toBe(true));
  });

  it('sums up the sheets per person, never per party', async () => {
    mockApi({ 'GET /api/print/stations': () => jsonResponse([{ ...station, devices: [{ ...station.devices[0],
      usage: [
        { name: null, isYou: true, sheets: 12, completed: 11, byPaper: { '10x15': 12 }, parties: 10, album: 0, tests: 2 },
        { name: 'Mario', isYou: false, sheets: 30, completed: 30, byPaper: { '10x15': 20, '20x15': 10 },
          parties: 30, album: 0, tests: 0 },
      ] }] }]) });
    render(view());
    const rows = await screen.findAllByTestId('print-usage-row');
    expect(rows[0]).toHaveTextContent('Tu Fogli: 12, stampati: 11 · 10×15: 12 · Feste: 10 · Test: 2');
    expect(rows[1]).toHaveTextContent('Mario Fogli: 30, stampati: 30 · 10×15: 20 · 20×15: 10 · Feste: 30');
    expect(rows[1]).not.toHaveTextContent('Album');
  });

  // --- Lending a printer ------------------------------------------------------

  const device = station.devices[0];
  const sharesUrl = `/api/print/stations/${station.id}/devices/${device.id}/shares`;
  const share = { id: 'sh-1', granteeName: 'Mario', granteeEmail: 'mario@example.com',
    maxSheets: 50, usedSheets: 8, createdAt: '2026-09-01T12:00:00Z' };

  it('lends the printer by email with an optional ceiling', async () => {
    const user = userEvent.setup();
    let shares: typeof share[] = [];
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([{ ...station, devices: [{ ...device, shares }] }]),
      [`POST ${sharesUrl}`]: () => { shares = [share]; return jsonResponse(share, 201); },
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Condivisione' }));
    await user.type(screen.getByLabelText('Email dell’account'), 'mario@example.com');
    await user.type(screen.getByLabelText('Tetto fogli (facoltativo)'), '50');
    await user.click(screen.getByRole('button', { name: 'Condividi' }));

    expect(await screen.findByText('Stampante condivisa.')).toBeInTheDocument();
    const post = mock.calls.find((c) => c.method === 'POST' && c.url === sharesUrl);
    expect(JSON.parse(post!.body!)).toEqual({ email: 'mario@example.com', maxSheets: 50 });
    expect(await screen.findByTestId('print-share-row')).toHaveTextContent('Fogli usati: 8 di 50');
    expect(screen.getByRole('button', { name: 'Condivisione (1)' })).toBeInTheDocument();
  });

  it('lends without a ceiling when none is typed, and says why a share was refused', async () => {
    const user = userEvent.setup();
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([station]),
      [`POST ${sharesUrl}`]: () => errorResponse(400, { error: 'recipient_not_found' }),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Condivisione' }));
    await user.type(screen.getByLabelText('Email dell’account'), 'nessuno@example.com');
    await user.click(screen.getByRole('button', { name: 'Condividi' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Nessun account NubArca con questa email.');
    expect(JSON.parse(mock.calls.find((c) => c.method === 'POST')!.body!))
      .toEqual({ email: 'nessuno@example.com', maxSheets: null });
  });

  it('changes a loan’s ceiling and ends the loan', async () => {
    const user = userEvent.setup();
    vi.spyOn(window, 'confirm').mockReturnValue(true);
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([{ ...station, devices: [{ ...device, shares: [share] }] }]),
      'PUT /api/print/shares/sh-1': () => jsonResponse({ ...share, maxSheets: 80 }),
      'DELETE /api/print/shares/sh-1': () => emptyResponse(204),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Condivisione (1)' }));
    const ceiling = screen.getByLabelText('Tetto fogli per Mario');
    await user.clear(ceiling);
    await user.type(ceiling, '80');
    await user.click(screen.getByRole('button', { name: 'Aggiorna tetto' }));
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'PUT')).toBe(true));
    expect(JSON.parse(mock.calls.find((c) => c.method === 'PUT')!.body!)).toEqual({ maxSheets: 80 });

    // The loan's own Revoke, not the station's.
    await user.click(within(screen.getByTestId('print-share-row')).getByRole('button', { name: 'Revoca' }));
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'DELETE'
      && c.url === '/api/print/shares/sh-1')).toBe(true));
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('Revocare la stampante a Mario?'));
  });

  // --- Printers lent to you ---------------------------------------------------

  const lent = {
    shareId: 'sh-9', stationId: '99999999-9999-9999-9999-999999999999', stationName: 'Casa di Stefano',
    stationStatus: 'online', deviceId: '88888888-8888-8888-8888-888888888888', displayName: 'DS-RX1HS',
    observedState: 'ready', ownerName: 'Stefano', loadedPaperSize: '10x15', papers: ['10x15', '20x15'],
    supportsPhoto10x15: true, cutsStrips: true, loadedPaperChangedBy: null, loadedPaperChangedAt: null,
    maxSheets: 10, usedSheets: 3,
  };

  it('shows a printer lent to you, with its paper, a test page and the sheets used', async () => {
    const user = userEvent.setup();
    const paperUrl = `/api/print/stations/${lent.stationId}/devices/${lent.deviceId}/paper`;
    const mock = mockApi({
      'GET /api/print/stations': () => jsonResponse([]),
      'GET /api/print/shared-printers': () => jsonResponse([lent]),
      [`PUT ${paperUrl}`]: () => jsonResponse({}),
      [`POST /api/print/stations/${lent.stationId}/test-jobs`]: () => jsonResponse(station.currentJob, 202),
    });
    render(view());
    const card = await screen.findByTestId('print-shared-printer');
    // No station of your own is not "nothing here" when one is lent to you.
    expect(screen.queryByTestId('print-empty')).not.toBeInTheDocument();
    expect(card).toHaveTextContent('Condivisa da Stefano');
    expect(within(card).getByTestId('print-shared-sheets')).toHaveTextContent('Fogli usati: 3 di 10');
    // Colours, pause and loans stay the owner's.
    expect(within(card).queryByRole('button', { name: 'Regola colori' })).not.toBeInTheDocument();
    expect(within(card).queryByRole('button', { name: 'Pausa' })).not.toBeInTheDocument();
    expect(within(card).queryByTestId('print-share')).not.toBeInTheDocument();

    await user.selectOptions(within(card).getByLabelText('Carta caricata'), '20x15');
    await waitFor(() => expect(mock.calls.some((c) => c.method === 'PUT' && c.url === paperUrl)).toBe(true));

    await user.click(within(card).getByRole('button', { name: 'Stampa pagina test' }));
    expect(await within(card).findByText('Pagina test in coda.')).toBeInTheDocument();
    expect(JSON.parse(mock.calls.find((c) => c.url.endsWith('/test-jobs'))!.body!))
      .toEqual({ printerDeviceId: lent.deviceId });
  });

  it('stops the test page when the loan’s sheets are used up', async () => {
    mockApi({
      'GET /api/print/stations': () => jsonResponse([]),
      'GET /api/print/shared-printers': () => jsonResponse([{ ...lent, usedSheets: 10 }]),
    });
    render(view());
    const card = await screen.findByTestId('print-shared-printer');
    expect(within(card).getByRole('button', { name: 'Stampa pagina test' })).toBeDisabled();
    expect(card).toHaveTextContent('Hai usato tutti i fogli di questa condivisione.');
  });

  it('says so when the server finds the sheets used up first', async () => {
    const user = userEvent.setup();
    mockApi({
      'GET /api/print/stations': () => jsonResponse([]),
      'GET /api/print/shared-printers': () => jsonResponse([lent]),
      [`POST /api/print/stations/${lent.stationId}/test-jobs`]: () => errorResponse(409, { error: 'share_exhausted' }),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Stampa pagina test' }));
    expect(await screen.findByRole('alert')).toHaveTextContent('Hai usato tutti i fogli di questa condivisione.');
  });
});

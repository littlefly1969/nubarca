import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PrintStationsPanel } from './PrintStationsPanel';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

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

function view() { return <AuthedWrapper><PrintStationsPanel /></AuthedWrapper>; }

describe('PrintStationsPanel', () => {
  it('renders the empty state', async () => {
    installFetchMock({ 'GET /api/print/stations': () => jsonResponse([]) });
    render(view());
    expect(await screen.findByTestId('print-empty')).toBeInTheDocument();
  });

  it('renders online status, printer, queue and current job', async () => {
    installFetchMock({ 'GET /api/print/stations': () => jsonResponse([station]) });
    render(view());
    expect(await screen.findByText('Studio')).toBeInTheDocument();
    expect(screen.getByText('Online')).toBeInTheDocument();
    expect(screen.getByText('DNP DS620')).toBeInTheDocument();
    expect(screen.getByText(/abc12345/)).toBeInTheDocument();
    // A DS620 without a cutting queue: strips come out as one sheet.
    expect(screen.getByTestId('print-strip-cut')).toHaveTextContent('Un foglio, da tagliare a mano');
  });

  it('adjusts the printer colours and saves them for the next sheets', async () => {
    const user = userEvent.setup();
    const device = station.devices[0];
    const url = `/api/print/stations/${station.id}/devices/${device.id}/calibration`;
    const mock = installFetchMock({
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
    installFetchMock({ 'GET /api/print/stations': () => jsonResponse([
      { ...station, devices: [{ ...station.devices[0], displayName: 'DNP DS-RX1HS', cutsStrips: true }] },
    ]) });
    render(view());
    expect(await screen.findByTestId('print-strip-cut'))
      .toHaveTextContent('Tagliate dalla stampante (2×6)');
  });

  it('renders offline status and the bounded last error', async () => {
    installFetchMock({ 'GET /api/print/stations': () => jsonResponse([
      { ...station, status: 'offline', lastError: 'printer_offline' },
    ]) });
    render(view());
    expect(await screen.findByText('Offline')).toBeInTheDocument();
    expect(screen.getByText('printer_offline')).toBeInTheDocument();
  });

  it('creates a station and exposes the one-shot enrollment command', async () => {
    const user = userEvent.setup();
    const mock = installFetchMock({
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
    const mock = installFetchMock({
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
    const mock = installFetchMock({
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
    const mock = installFetchMock({
      'GET /api/print/stations': () => jsonResponse([station]),
      [`POST /api/print/stations/${station.id}/test-jobs`]: () => jsonResponse(station.currentJob, 202),
    });
    render(view());
    await user.click(await screen.findByRole('button', { name: 'Stampa pagina test' }));
    await waitFor(() => expect(mock.calls.some((x) => x.method === 'POST'
      && x.url.includes('/test-jobs'))).toBe(true));
  });

  it('does not offer a test print to an offline or incompatible device', async () => {
    installFetchMock({ 'GET /api/print/stations': () => jsonResponse([
      { ...station, devices: [{ ...station.devices[0], observedState: 'offline' }] },
    ]) });
    render(view());
    expect(await screen.findByRole('button', { name: 'Stampa pagina test' })).toBeDisabled();
  });
});

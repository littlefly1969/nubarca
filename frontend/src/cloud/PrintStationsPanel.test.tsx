import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
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

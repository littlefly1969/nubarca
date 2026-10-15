import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { AdminUpdatesPage } from './AdminUpdatesPage';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const CANDIDATE = '0123456789abcdef0123456789abcdef01234567';
const RELEASE = '89abcdef'.repeat(8);

function described(overrides: Record<string, unknown> = {}) {
  return {
    runningVersion: '0.3.0',
    runningSha: '1111111111111111111111111111111111111111',
    updaterConnected: true,
    state: 'idle',
    heartbeatAt: '2026-10-15T10:00:00Z',
    requestPending: false,
    current: null,
    last: null,
    lastCheckAt: '2026-10-15T09:00:00Z',
    autoCheckHours: 6,
    report: {
      outcome: 'ready',
      reason: null,
      checkedAt: '2026-10-15T09:00:00Z',
      candidateSha: CANDIDATE,
      candidateVersion: '0.4.0',
      signed: true,
      releaseId: RELEASE,
      currentVersion: '0.3.0',
      apiVariant: 'openvino',
      components: { backend: true, frontend: false, printAgent: true, printAgentVersion: '0.9.0', tv: false, tvEnabled: false },
      migrations: ['20261015101023_AddPrintAgentSelfUpdate'],
      confirmMigrations: true,
      commits: [{ sha: '0123456', subject: 'feat: something' }],
    },
    ...overrides,
  };
}

const box = (overrides: Record<string, unknown> = {}) => ({
  stationId: 'b0000000-0000-0000-0000-000000000001',
  name: 'Sala',
  ownerName: 'Stefano',
  status: 'online',
  agent: {
    version: '0.9.0', build: null, compatibility: 'update-available', unofficial: false, latestVersion: '0.10.0',
    selfUpdate: true, updateTarget: null, updateRequestedAt: null, updateResult: null,
    updateResultVersion: null, updateResultAt: null,
  },
  canUpdate: true,
  ...overrides,
});

function renderPage() {
  render(<AuthedWrapper isAdmin><AdminUpdatesPage /></AuthedWrapper>);
}

describe('AdminUpdatesPage', () => {
  it('installs only after the migrations the check found are confirmed', async () => {
    let pending = false;
    const mock = installFetchMock({
      'GET /api/admin/system-update': () => jsonResponse(described({ requestPending: pending })),
      'GET /api/admin/system-update/print-boxes': () => jsonResponse([]),
      'POST /api/admin/system-update/apply': () => {
        pending = true;
        return jsonResponse(described({ requestPending: true }), 202);
      },
    });
    renderPage();

    const install = await screen.findByTestId('updates-install');
    expect(install).toBeDisabled();
    expect(screen.getByTestId('updates-outcome').textContent).toContain('0.4.0');

    fireEvent.click(screen.getByTestId('updates-confirm-migrations'));
    expect(install).toBeEnabled();
    fireEvent.click(install);

    await waitFor(() => expect(mock.calls.some((c) => c.method === 'POST')).toBe(true));
    const post = mock.calls.find((c) => c.method === 'POST')!;
    expect(JSON.parse(post.body!)).toEqual({ sha: CANDIDATE, releaseId: RELEASE, confirmMigrations: true });
    expect(await screen.findByTestId('updates-busy')).toBeInTheDocument();
  });

  it('does not carry a migration confirmation over to another release', async () => {
    let report = described().report;
    installFetchMock({
      'GET /api/admin/system-update': () => jsonResponse(described({ report })),
      'GET /api/admin/system-update/print-boxes': () => jsonResponse([]),
    });
    renderPage();
    fireEvent.click(await screen.findByTestId('updates-confirm-migrations'));
    expect(screen.getByTestId('updates-install')).toBeEnabled();

    // A newer check finds another release with other migrations.
    report = { ...report, candidateSha: 'f'.repeat(40), candidateVersion: '0.5.0', checkedAt: '2026-10-15T11:00:00Z',
      releaseId: 'e'.repeat(64), migrations: ['20261016000000_Another'] };
    fireEvent.click(screen.getByRole('button', { name: 'Aggiorna' }));
    await waitFor(() => expect(screen.getByTestId('updates-outcome').textContent).toContain('0.5.0'));
    expect(screen.getByTestId('updates-confirm-migrations')).not.toBeChecked();
    expect(screen.getByTestId('updates-install')).toBeDisabled();
  });

  it('offers nothing to run without the host updater', async () => {
    installFetchMock({
      'GET /api/admin/system-update': () => jsonResponse(described({ updaterConnected: false, state: null })),
      'GET /api/admin/system-update/print-boxes': () => jsonResponse([]),
    });
    renderPage();
    expect(await screen.findByTestId('updates-check')).toBeDisabled();
    expect(screen.getByTestId('updates-install')).toBeDisabled();
  });

  it('shows why a refused request was refused', async () => {
    installFetchMock({
      'GET /api/admin/system-update': () => jsonResponse(described({ report: null })),
      'GET /api/admin/system-update/print-boxes': () => jsonResponse([]),
      'POST /api/admin/system-update/check': () => errorResponse(409, { error: 'update_busy' }),
    });
    renderPage();
    fireEvent.click(await screen.findByTestId('updates-check'));
    expect((await screen.findByTestId('updates-message')).textContent).not.toBe('');
    expect(screen.queryByTestId('updates-install')).toBeNull();
  });

  it('asks a Print Box, or all of them, to update', async () => {
    const mock = installFetchMock({
      'GET /api/admin/system-update': () => jsonResponse(described({ report: null })),
      'GET /api/admin/system-update/print-boxes': () => jsonResponse([
        box(),
        box({
          stationId: 'b0000000-0000-0000-0000-000000000002', name: 'Bar', canUpdate: false,
          agent: { ...box().agent, selfUpdate: false },
        }),
      ]),
      'POST /api/admin/system-update/print-boxes': () => jsonResponse({ requested: 1, version: '0.10.0' }),
    });
    renderPage();
    expect(await screen.findAllByTestId('updates-box')).toHaveLength(2);
    fireEvent.click(screen.getByTestId('updates-boxes-all'));
    await waitFor(() => expect(mock.calls.some((c) =>
      c.method === 'POST' && c.url.endsWith('/api/admin/system-update/print-boxes'))).toBe(true));
  });
});

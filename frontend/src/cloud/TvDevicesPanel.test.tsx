import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { TvDevicesPanel } from './TvDevicesPanel';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const activeDevice = {
  id: 's1',
  deviceLabel: 'Living room TV',
  userAgent: 'Mozilla/5.0 (SmartTV)',
  status: 'active',
  createdAt: '2026-07-01T10:00:00Z',
  lastSeenAt: '2026-07-05T10:00:00Z',
  expiresAt: '2026-08-01T10:00:00Z',
  revokedAt: null,
};

function wrapper() {
  return (
    <AuthedWrapper>
      <MemoryRouter><TvDevicesPanel /></MemoryRouter>
    </AuthedWrapper>
  );
}

describe('TvDevicesPanel', () => {
  it('shows the empty state when no TVs are paired', async () => {
    installFetchMock({ 'GET /api/tv-personal/pin': () => jsonResponse({ configured: true, updatedAt: '2026-07-01T10:00:00Z' }), 'GET /api/tv-devices': () => jsonResponse([]) });
    render(wrapper());
    expect(await screen.findByTestId('tv-devices-empty')).toBeInTheDocument();
  });

  it('lists an active paired TV without exposing internals', async () => {
    const mock = installFetchMock({ 'GET /api/tv-personal/pin': () => jsonResponse({ configured: true, updatedAt: '2026-07-01T10:00:00Z' }), 'GET /api/tv-devices': () => jsonResponse([activeDevice]) });
    render(wrapper());

    expect(await screen.findByText('Living room TV')).toBeInTheDocument();
    expect(screen.getByText('Attiva')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Revoca' })).toBeInTheDocument();
    // No token/hash/secret leaked into the DOM.
    expect(document.body.innerHTML).not.toMatch(/tokenhash|sessiontoken|secret/i);
    expect(mock.calls.some((c) => c.url.includes('/api/tv-devices'))).toBe(true);
  });

  it('revokes a TV after confirmation and refreshes the list', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('confirm', () => true);
    let listCall = 0;
    const mock = installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse({ configured: true, updatedAt: '2026-07-01T10:00:00Z' }),
      'GET /api/tv-devices': () => jsonResponse(
        listCall++ === 0 ? [activeDevice] : [{ ...activeDevice, status: 'revoked', revokedAt: '2026-07-06T10:00:00Z' }],
      ),
      'DELETE /api/tv-devices/s1': () => emptyResponse(204),
    });

    render(wrapper());
    await user.click(await screen.findByRole('button', { name: 'Revoca' }));

    // The revoke request was sent, and after refresh the row is Revoked (no Revoke button).
    await waitFor(() =>
      expect(mock.calls.some((c) => c.method === 'DELETE' && c.url.includes('/api/tv-devices/s1'))).toBe(true));
    expect(await screen.findByText('Revocata')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'Revoca' })).not.toBeInTheDocument();
  });

  it('does not revoke when confirmation is cancelled', async () => {
    const user = userEvent.setup();
    vi.stubGlobal('confirm', () => false);
    const mock = installFetchMock({ 'GET /api/tv-personal/pin': () => jsonResponse({ configured: true, updatedAt: '2026-07-01T10:00:00Z' }), 'GET /api/tv-devices': () => jsonResponse([activeDevice]) });

    render(wrapper());
    await user.click(await screen.findByRole('button', { name: 'Revoca' }));

    expect(mock.calls.some((c) => c.method === 'DELETE')).toBe(false);
    expect(screen.getByText('Living room TV')).toBeInTheDocument();
  });
});

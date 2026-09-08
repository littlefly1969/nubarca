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

const generalAssignment = {
  kind: 'general', albumId: null, albumName: null, partyAvailable: false,
};

const activeDevice = {
  id: 's1',
  deviceLabel: 'Living room TV',
  userAgent: 'Mozilla/5.0 (SmartTV)',
  status: 'active',
  createdAt: '2026-07-01T10:00:00Z',
  lastSeenAt: '2026-07-05T10:00:00Z',
  expiresAt: '2026-08-01T10:00:00Z',
  revokedAt: null,
  assignment: generalAssignment,
};

const PIN = { configured: true, updatedAt: '2026-07-01T10:00:00Z' };
const PARTIES = [
  { albumId: 'a1', albumName: 'Festa di Anna', gameEnabled: true },
  { albumId: 'a2', albumName: 'Compleanno', gameEnabled: false },
];

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

  it('offers the TV\'s use as a choice, and sends the ALBUM rather than a link', async () => {
    let sent: unknown = null;
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse(PIN),
      'GET /api/tv-devices': () => jsonResponse([activeDevice]),
      'GET /api/tv-devices/parties': () => jsonResponse(PARTIES),
      'PATCH /api/tv-devices/s1/assignment': ({ body }: { body: string | null }) => {
        sent = JSON.parse(body ?? '{}');
        return jsonResponse({
          kind: 'party', albumId: 'a1', albumName: 'Festa di Anna', partyAvailable: true,
        });
      },
    });
    render(wrapper());
    const user = userEvent.setup();

    const select = await screen.findByLabelText(/come usi questa tv/i);
    // The general experience is always offered, and every live party beside it.
    expect(select).toHaveValue('');
    await user.selectOptions(select, 'a1');

    // A party is named by ALBUM. A party link id is internal and never crosses.
    await waitFor(() => expect(sent).toEqual({ kind: 'party', albumId: 'a1' }));
    // The row adopts what the SERVER returned, without a refetch of the list.
    await waitFor(() => expect(screen.getByLabelText(/come usi questa tv/i)).toHaveValue('a1'));
    expect(document.body.innerHTML).not.toMatch(/linkid|tokenhash/i);
  });

  it('says a party is gone rather than quietly reading as a general TV', async () => {
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse(PIN),
      'GET /api/tv-devices': () => jsonResponse([{
        ...activeDevice,
        assignment: {
          kind: 'party', albumId: 'a9', albumName: 'Festa finita', partyAvailable: false,
        },
      }]),
      // The revoked party is NOT assignable any more…
      'GET /api/tv-devices/parties': () => jsonResponse(PARTIES),
    });
    render(wrapper());

    expect(await screen.findByTestId('tv-device-party-gone-s1')).toBeInTheDocument();
    // …but the select still shows what this TV is actually set to, rather than
    // falling back to "NubArca TV" and lying about it.
    expect(screen.getByLabelText(/come usi questa tv/i)).toHaveValue('a9');
  });

  it('reads a device carrying no assignment as a general TV, not as a crash', async () => {
    // An older server simply does not send the field. That absence IS the
    // answer — it is a general television — and it must not take the panel down.
    const { assignment, ...legacyDevice } = activeDevice;
    void assignment;
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse(PIN),
      'GET /api/tv-devices': () => jsonResponse([legacyDevice]),
      'GET /api/tv-devices/parties': () => jsonResponse(PARTIES),
    });
    render(wrapper());

    expect(await screen.findByText('Living room TV')).toBeInTheDocument();
    expect(screen.getByLabelText(/come usi questa tv/i)).toHaveValue('');
    expect(screen.queryByTestId('tv-device-party-gone-s1')).not.toBeInTheDocument();
  });

  it('offers only the general experience when there is no live party', async () => {
    installFetchMock({
      'GET /api/tv-personal/pin': () => jsonResponse(PIN),
      'GET /api/tv-devices': () => jsonResponse([activeDevice]),
      'GET /api/tv-devices/parties': () => jsonResponse([]),
    });
    render(wrapper());
    await screen.findByText('Living room TV');
    expect(screen.getByLabelText(/come usi questa tv/i)).toHaveValue('');
    expect(screen.getByText(/nessuna festa attiva/i)).toBeInTheDocument();
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

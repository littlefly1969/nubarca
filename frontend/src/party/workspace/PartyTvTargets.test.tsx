import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { PartyTvTargets } from './PartyTvTargets';

/**
 * WHICH TELEVISION SHOWS THIS PARTY, answered where it is asked.
 *
 * What these defend:
 *   * a host reads the CURRENT destination of every screen before touching
 *     one, so turning a television on for tonight is never a guess about what
 *     it was doing;
 *   * the switch sends the party's ALBUM — the server never takes a party link
 *     id from a client — and switching off sends `general`, not nothing;
 *   * a television already pointed at ANOTHER party says whose, because that is
 *     exactly the move a host must not make by accident;
 *   * an expired or revoked pairing is not offered: it can show nothing, and a
 *     row that does nothing is worse than no row;
 *   * with no television at all the panel sends the host to the one page that
 *     pairs one, which is the single case where leaving this section is right.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ALBUM_ID = 'album-tonight';

function device(over: Record<string, unknown> = {}) {
  return {
    id: 'tv-1',
    deviceLabel: 'Salone',
    userAgent: null,
    status: 'active',
    createdAt: '2026-09-01T10:00:00Z',
    lastSeenAt: '2026-09-21T20:00:00Z',
    expiresAt: '2026-12-01T10:00:00Z',
    revokedAt: null,
    assignment: { kind: 'general', albumId: null, albumName: null, partyAvailable: false },
    ...over,
  };
}

function show(devices: unknown[], extra: Record<string, unknown> = {}) {
  const mock = installFetchMock({
    'GET /api/tv-devices': () => jsonResponse(devices),
    ...extra,
  });
  render(
    <MemoryRouter>
      <AuthedWrapper><PartyTvTargets albumId={ALBUM_ID} /></AuthedWrapper>
    </MemoryRouter>,
  );
  return mock;
}

it('names every television and what it is showing right now', async () => {
  show([
    device(),
    device({
      id: 'tv-2',
      deviceLabel: 'Giardino',
      assignment: {
        kind: 'party', albumId: 'another', albumName: 'Laurea di Anna', partyAvailable: true,
      },
    }),
    device({
      id: 'tv-3',
      deviceLabel: 'Sala',
      assignment: {
        kind: 'party', albumId: ALBUM_ID, albumName: 'Stasera', partyAvailable: true,
      },
    }),
  ]);

  await screen.findByTestId('party-tv-target-tv-1-row');

  // General, another party (named), and this one — three different answers.
  expect(screen.getByTestId('party-tv-target-tv-1-row')).toHaveTextContent('NubArca TV');
  expect(screen.getByTestId('party-tv-target-tv-2-row')).toHaveTextContent('Laurea di Anna');
  expect(screen.getByTestId('party-tv-target-tv-3-row')).toHaveTextContent('questa festa');

  // Only the one pointed here is on.
  expect(screen.getByTestId('party-tv-target-tv-1')).toHaveAttribute('aria-checked', 'false');
  expect(screen.getByTestId('party-tv-target-tv-2')).toHaveAttribute('aria-checked', 'false');
  expect(screen.getByTestId('party-tv-target-tv-3')).toHaveAttribute('aria-checked', 'true');
});

it('points a television here by ALBUM, and releases it to the general view', async () => {
  const user = userEvent.setup();
  // The server answers with the NEW assignment, so the second click needs a
  // different answer from the first: one mutable reply, not two handlers.
  let reply: unknown = {
    kind: 'party', albumId: ALBUM_ID, albumName: 'Stasera', partyAvailable: true,
  };
  const mock = show([device()], {
    'PATCH /api/tv-devices/tv-1/assignment': () => jsonResponse(reply),
  });

  await user.click(await screen.findByTestId('party-tv-target-tv-1'));

  await waitFor(() => {
    expect(screen.getByTestId('party-tv-target-tv-1')).toHaveAttribute('aria-checked', 'true');
  });
  // The album travels; a party link id never does.
  const on = mock.calls.find((c) => c.method === 'PATCH');
  expect(JSON.parse(String(on?.body))).toEqual({ kind: 'party', albumId: ALBUM_ID });

  // And off is `general`, which is a destination — not an absent one.
  reply = { kind: 'general', albumId: null, albumName: null, partyAvailable: false };
  await user.click(screen.getByTestId('party-tv-target-tv-1'));

  await waitFor(() => {
    expect(screen.getByTestId('party-tv-target-tv-1')).toHaveAttribute('aria-checked', 'false');
  });
  expect(JSON.parse(String(mock.calls.filter((c) => c.method === 'PATCH')[1]?.body)))
    .toEqual({ kind: 'general' });
});

it('does not offer a pairing that can show nothing', async () => {
  show([
    device({ id: 'tv-dead', deviceLabel: 'Vecchia', status: 'revoked' }),
    device({ id: 'tv-old', deviceLabel: 'Scaduta', status: 'expired' }),
  ]);

  await screen.findByTestId('party-tv-targets-empty');
  expect(screen.queryByTestId('party-tv-target-tv-dead-row')).toBeNull();
  expect(screen.queryByTestId('party-tv-target-tv-old-row')).toBeNull();
});

it('sends a host with no television to the one page that pairs one', async () => {
  show([]);

  const empty = await screen.findByTestId('party-tv-targets-empty');
  expect(empty).toHaveTextContent('Nessun televisore abbinato');
  expect(screen.getByRole('link', { name: /Abbina un televisore/i }))
    .toHaveAttribute('href', expect.stringContaining('tv-devices'));
});

it('keeps the rows when a change fails, and says so', async () => {
  const user = userEvent.setup();
  show([device()], {
    'PATCH /api/tv-devices/tv-1/assignment': () => new Response('no', { status: 500 }),
  });

  await user.click(await screen.findByTestId('party-tv-target-tv-1'));

  await screen.findByTestId('party-tv-targets-failed');
  // The switch did NOT move: the television is where it was.
  expect(screen.getByTestId('party-tv-target-tv-1')).toHaveAttribute('aria-checked', 'false');
});

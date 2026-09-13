import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import type { PartyGamePublicSnapshot } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';
import { PartyGameAffordance, partyGameBeat } from './PartyGameAffordance';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.useRealTimers(); });

const TOKEN = 'tok-1';
const READ = `/api/party/${TOKEN}/game`;

function snapshot(over: Partial<PartyGamePublicSnapshot> = {}): PartyGamePublicSnapshot {
  return {
    albumName: 'Festa', status: 'live', phase: 'challenge_active', version: 4,
    roundNumber: 1, totalChallenges: 3, phaseEndsAt: null, roundId: 'r1',
    myVote: null, voting: null, preferences: null, challenge: null,
    ...over,
  };
}

function mount() {
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${TOKEN}`]}>
        <Routes>
          <Route
            path="/party/:token"
            element={<PartyGameAffordance token={TOKEN} gameUrl={`/party/${TOKEN}/game`} />}
          />
          <Route path="/party/:token/game" element={<p>il game</p>} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

describe('what the party says the game is doing', () => {
  // The pure rule first: four words, and the one state that has no door.
  it.each([
    ['lobby with preferences open', { phase: 'lobby', preferences: { open: true } }, 'choose'],
    ['lobby with preferences closed', { phase: 'lobby', preferences: { open: false } }, 'live'],
    ['lobby with no preferences at all', { phase: 'lobby', preferences: null }, 'live'],
    ['an activity running', { phase: 'challenge_active', preferences: null }, 'live'],
    ['voting open', { phase: 'voting_open', preferences: null }, 'vote'],
    ['an intermission', { phase: 'intermission', preferences: null }, 'paused'],
  ])('reads %s as "%s"', (_name, partial, expected) => {
    expect(partyGameBeat({ status: 'live', ...partial } as never)).toBe(expected);
  });

  it('offers no door once the match is over', () => {
    // A dead CTA is the one thing every Party surface refuses to render.
    expect(partyGameBeat({ status: 'finished', phase: 'finished', preferences: null })).toBeNull();
    expect(partyGameBeat(null)).toBeNull();
  });

  it('is a link into the game, and never a redirect', async () => {
    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(snapshot({ phase: 'voting_open' })) });
    mount();
    const bar = await screen.findByTestId('party-game-affordance');
    expect(bar).toHaveAttribute('href', `/party/${TOKEN}/game`);
    expect(bar).toHaveAttribute('data-beat', 'vote');
    expect(bar).toHaveTextContent(/vota ora/i);
    // The guest is still on the party. Nothing took the phone.
    expect(screen.queryByText('il game')).not.toBeInTheDocument();
  });

  it('never joins, so reading the party does not put anybody in the room', async () => {
    // A hub that minted a participant would count everybody who ever opened the
    // party as being in the room — the one number the control room reads out
    // loud during an evening.
    const mock = installFetchMock({ [`GET ${READ}`]: () => jsonResponse(snapshot()) });
    mount();
    await screen.findByTestId('party-game-affordance');
    expect(mock.calls.some((c) => c.url.includes('/game/join'))).toBe(false);
    expect(mock.calls.every((c) => c.method === 'GET')).toBe(true);
  });

  it('needs no second QR: the same browser walks in on what it is already holding', async () => {
    // The bar addresses the party's OWN view token. There is no new token, no
    // pairing step and no second code — which is the whole point: a guest who
    // scanned once must not have to find the card on the table again.
    const mock = installFetchMock({ [`GET ${READ}`]: () => jsonResponse(snapshot()) });
    mount();
    const bar = await screen.findByTestId('party-game-affordance');
    expect(bar.getAttribute('href')).toBe(`/party/${TOKEN}/game`);
    expect(mock.calls.every((c) => c.url.startsWith(`/api/party/${TOKEN}/`))).toBe(true);
  });

  it('follows the party without anybody reloading it', async () => {
    // `shouldAdvanceTime` so waitFor's own interval still runs under fake
    // timers; without it the assertion below waits for a clock nobody winds.
    vi.useFakeTimers({ shouldAdvanceTime: true });
    let current = snapshot({ phase: 'lobby', preferences: openPreferences() });
    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(current) });
    mount();
    await waitFor(() => expect(screen.getByTestId('party-game-affordance'))
      .toHaveAttribute('data-beat', 'choose'));

    current = snapshot({ phase: 'voting_open' });
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(screen.getByTestId('party-game-affordance')).toHaveAttribute('data-beat', 'vote');

    current = snapshot({ phase: 'intermission' });
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(screen.getByTestId('party-game-affordance')).toHaveAttribute('data-beat', 'paused');
    expect(screen.getByTestId('party-game-affordance')).toHaveTextContent(/pausa/i);

    current = snapshot({ status: 'finished', phase: 'finished' });
    await act(async () => { await vi.advanceTimersByTimeAsync(10_000); });
    expect(screen.queryByTestId('party-game-affordance')).not.toBeInTheDocument();
  });
});

function openPreferences() {
  return { open: true, votesPerGuest: 3, votesUsed: 0, votesRemaining: 3, items: [] };
}

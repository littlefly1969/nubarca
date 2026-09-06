import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import type { PartyGameSnapshot } from '@nubarca/api-client';
import { partyGameDisplayState } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyControlRoomPage } from './PartyControlRoomPage';

vi.mock('qrcode', () => ({ default: { toString: () => Promise.resolve('<svg />') } }));

const ALBUM = 'a1';
const READ = `/api/albums/${ALBUM}/party-game`;
const COMMANDS = `/api/albums/${ALBUM}/party-game/commands`;

function activity(over: Partial<NonNullable<PartyGameSnapshot['currentChallenge']>> = {}) {
  return {
    id: 'c1', title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare' as const,
    mediaUrl: null, durationSeconds: null, votingMode: 'binary' as const,
    voteQuestion: null, ...over,
  };
}

function snapshot(over: Partial<PartyGameSnapshot> = {}): PartyGameSnapshot {
  return {
    albumId: ALBUM, sessionId: 's1', status: 'live', phase: 'challenge_reveal',
    version: 3, roundNumber: 1, totalChallenges: 4, playedRounds: 0,
    startedAt: '2026-09-06T20:00:00Z', finishedAt: null,
    phaseStartedAt: '2026-09-06T20:00:00Z', phaseEndsAt: null,
    currentChallenge: activity(), nextChallenge: activity({ id: 'c2', title: 'Ballo' }),
    availableCommands: ['start_challenge', 'skip_challenge', 'finish'],
    voting: null, guestsPresent: 7, displaySeenSecondsAgo: 2,
    tvUrl: '/party/tok-1/tv', guestUrl: '/party/tok-1/game',
    ...over,
  };
}

async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

function mount() {
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[`/albums/${ALBUM}/party-game`]}>
        <Routes>
          <Route path="/albums/:albumId/party-game" element={<PartyControlRoomPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

beforeEach(() => { vi.useFakeTimers({ shouldAdvanceTime: true }); });
afterEach(() => { vi.useRealTimers(); cleanup(); vi.unstubAllGlobals(); });

describe('the control room', () => {
  it('offers exactly the commands the server says are legal, in its order', async () => {
    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(snapshot()) });
    mount();

    const primary = await screen.findByTestId('party-control-primary');
    expect(primary).toHaveAttribute('data-command', 'start_challenge');
    expect(primary).toHaveTextContent(/avvia attività/i);
    expect(screen.getByTestId('party-control-skip_challenge')).toBeInTheDocument();
    expect(screen.getByTestId('party-control-finish')).toBeInTheDocument();
  });

  it('makes an illegal command absent, not disabled', async () => {
    // The state machine is not re-implemented here; availableCommands is quoted.
    installFetchMock({
      [`GET ${READ}`]: () => jsonResponse(snapshot({
        phase: 'challenge_active',
        currentChallenge: activity({ votingMode: 'none' }),
        availableCommands: ['reveal_result', 'skip_challenge', 'finish'],
      })),
    });
    mount();
    expect(await screen.findByTestId('party-control-primary'))
      .toHaveAttribute('data-command', 'reveal_result');
    expect(screen.queryByTestId('party-control-open_voting')).not.toBeInTheDocument();
    // Nothing on the page is present-but-dead.
    expect(document.querySelectorAll('button[disabled]')).toHaveLength(0);
  });

  it('carries the version the screen is showing, and never guesses the next phase', async () => {
    let current = snapshot();
    const sent: unknown[] = [];
    installFetchMock({
      [`GET ${READ}`]: () => jsonResponse(current),
      [`POST ${COMMANDS}`]: ({ body }: { body: string | null }) => {
        sent.push(JSON.parse(body ?? '{}'));
        current = snapshot({
          phase: 'challenge_active', version: 4,
          availableCommands: ['open_voting', 'skip_challenge', 'finish'],
        });
        return jsonResponse(current);
      },
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(await screen.findByTestId('party-control-primary'));

    await waitFor(() => expect(sent).toEqual([{ command: 'start_challenge', expectedVersion: 3 }]));
    await waitFor(() => expect(screen.getByTestId('party-control-primary'))
      .toHaveAttribute('data-command', 'open_voting'));
  });

  it('recovers from a stale command in the same round trip', async () => {
    // Another tab moved the game. The refusal carries the truth, so this screen
    // ends CORRECT rather than merely told off — and it does not fire a second
    // request to find out.
    let reads = 0;
    installFetchMock({
      [`GET ${READ}`]: () => { reads += 1; return jsonResponse(snapshot()); },
      [`POST ${COMMANDS}`]: () => errorResponse(409, {
        code: 'version_conflict',
        snapshot: snapshot({
          phase: 'voting_open', version: 5, roundNumber: 2,
          availableCommands: ['close_voting', 'skip_challenge', 'finish'],
          voting: { received: 4, eligible: 9, yes: null, no: null, passed: null },
        }),
      }),
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await screen.findByTestId('party-control-primary');
    const readsBefore = reads;
    await user.click(screen.getByTestId('party-control-primary'));

    expect(await screen.findByTestId('party-control-refusal'))
      .toHaveTextContent(/già andato avanti/i);
    expect(screen.getByTestId('party-control-primary'))
      .toHaveAttribute('data-command', 'close_voting');
    expect(screen.getByTestId('party-control-votes')).toHaveTextContent('4');
    expect(reads).toBe(readsBefore);
  });

  it('does not duplicate an action when the same command is sent twice', async () => {
    let version = 3;
    const sent: number[] = [];
    installFetchMock({
      [`GET ${READ}`]: () => jsonResponse(snapshot({ version })),
      [`POST ${COMMANDS}`]: ({ body }: { body: string | null }) => {
        const quoted = JSON.parse(body ?? '{}').expectedVersion as number;
        sent.push(quoted);
        if (quoted !== version) {
          return errorResponse(409, { code: 'version_conflict', snapshot: snapshot({ version }) });
        }
        version += 1;
        return jsonResponse(snapshot({ version, phase: 'challenge_active' }));
      },
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    const primary = await screen.findByTestId('party-control-primary');
    await user.click(primary);
    await waitFor(() => expect(sent).toHaveLength(1));
    // The version has moved; the game advanced exactly once.
    expect(version).toBe(4);
  });

  it('shows the room: guests, the screen, and how far the evening has got', async () => {
    installFetchMock({
      [`GET ${READ}`]: () => jsonResponse(snapshot({ guestsPresent: 12, playedRounds: 2 })),
    });
    mount();
    const room = await screen.findByTestId('party-control-room-state');
    expect(room).toHaveTextContent('12');
    expect(room).toHaveTextContent(/invitati collegati/i);
    expect(room).toHaveTextContent(/2/);
    expect(screen.getByTestId('party-control-tv')).toHaveTextContent(/schermo collegato/i);
    expect(screen.getByRole('link', { name: /apri lo schermo/i }))
      .toHaveAttribute('href', '/party/tok-1/tv');
  });

  it('says when the screen has stalled or was never there', async () => {
    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(snapshot({ displaySeenSecondsAgo: 40 })) });
    const stalled = render(<I18nProvider><MemoryRouter initialEntries={[`/albums/${ALBUM}/party-game`]}>
      <Routes><Route path="/albums/:albumId/party-game" element={<PartyControlRoomPage />} /></Routes>
    </MemoryRouter></I18nProvider>);
    expect(await screen.findByTestId('party-control-tv')).toHaveTextContent(/schermo fermo/i);
    stalled.unmount();

    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(snapshot({ displaySeenSecondsAgo: null })) });
    mount();
    expect(await screen.findByTestId('party-control-tv')).toHaveTextContent(/nessuno schermo/i);
  });

  it('counts votes without the split while voting is open, and with it once closed', async () => {
    let current = snapshot({
      phase: 'voting_open',
      availableCommands: ['close_voting', 'skip_challenge', 'finish'],
      voting: { received: 8, eligible: 12, yes: null, no: null, passed: null },
    });
    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(current) });
    mount();
    const votes = await screen.findByTestId('party-control-votes');
    expect(votes).toHaveTextContent('8');
    expect(votes).toHaveTextContent('/ 12');
    expect(screen.queryByTestId('party-control-split')).not.toBeInTheDocument();

    // Closing hands the host the split, because the host decides when to reveal.
    current = snapshot({
      phase: 'voting_closed',
      availableCommands: ['reveal_result', 'skip_challenge', 'finish'],
      voting: { received: 11, eligible: 12, yes: 9, no: 2, passed: true },
    });
    await advance(3_000);
    expect(await screen.findByTestId('party-control-split')).toHaveTextContent('9 sì · 2 no');
  });

  it('previews what is coming and admits when nothing is', async () => {
    let current = snapshot();
    installFetchMock({ [`GET ${READ}`]: () => jsonResponse(current) });
    mount();
    expect(await screen.findByTestId('party-control-next')).toHaveTextContent('Ballo');

    current = snapshot({ nextChallenge: null });
    await advance(3_000);
    await waitFor(() => expect(screen.getByText(/era l’ultima attività/i)).toBeInTheDocument());
  });

  it('asks before ending the evening', async () => {
    const sent: string[] = [];
    installFetchMock({
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
      [`POST ${COMMANDS}`]: ({ body }: { body: string | null }) => {
        sent.push(JSON.parse(body ?? '{}').command as string);
        return jsonResponse(snapshot({ status: 'finished', phase: 'finished', version: 4, availableCommands: [] }));
      },
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(await screen.findByTestId('party-control-finish'));

    const dialog = screen.getByTestId('party-control-finish-dialog');
    expect(within(dialog).getByText(/non può essere ripreso/i)).toBeInTheDocument();
    expect(sent).toHaveLength(0);

    await user.click(within(dialog).getByRole('button', { name: /annulla/i }));
    expect(sent).toHaveLength(0);

    await user.click(screen.getByTestId('party-control-finish'));
    await user.click(screen.getByTestId('party-control-finish-confirm'));
    await waitFor(() => expect(sent).toEqual(['finish']));
  });

  it('offers nothing at all once the game is over', async () => {
    installFetchMock({
      [`GET ${READ}`]: () => jsonResponse(snapshot({
        status: 'finished', phase: 'finished', availableCommands: [],
        currentChallenge: null, nextChallenge: null,
      })),
    });
    mount();
    expect(await screen.findByTestId('party-control-status')).toHaveTextContent(/concluso/i);
    expect(screen.queryByTestId('party-control-primary')).not.toBeInTheDocument();
    expect(screen.getByText(/il gioco è concluso/i)).toBeInTheDocument();
  });

  it('says plainly when the game is not switched on for this album', async () => {
    installFetchMock({ [`GET ${READ}`]: () => errorResponse(404) });
    mount();
    expect(await screen.findByText(/non è attivo su questo album/i)).toBeInTheDocument();
  });

  it('keeps the evening on screen when a poll fails', async () => {
    let fail = false;
    installFetchMock({
      [`GET ${READ}`]: () => (fail ? errorResponse(503) : jsonResponse(snapshot())),
    });
    mount();
    await screen.findByTestId('party-control-primary');
    fail = true;
    await advance(3_000);
    await waitFor(() => expect(screen.getByText(/riconnessione/i)).toBeInTheDocument());
    expect(screen.getByTestId('party-control-primary')).toBeInTheDocument();
  });
});

describe('partyGameDisplayState', () => {
  it('reads a polling gap as a screen that is there, stopped, or gone', () => {
    expect(partyGameDisplayState(0)).toBe('connected');
    expect(partyGameDisplayState(15)).toBe('connected');
    expect(partyGameDisplayState(16)).toBe('stale');
    expect(partyGameDisplayState(120)).toBe('stale');
    expect(partyGameDisplayState(121)).toBe('disconnected');
    // Never seen is not the same as seen a long time ago, but the host needs
    // one answer and it is the same one.
    expect(partyGameDisplayState(null)).toBe('disconnected');
  });
});

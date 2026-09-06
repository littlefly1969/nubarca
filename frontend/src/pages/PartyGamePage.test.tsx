import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import type { PartyGamePublicSnapshot } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyGamePage, guestScene } from './PartyGamePage';

const TOKEN = 'tok-1';
const READ = `/api/party/${TOKEN}/game`;
const JOIN = `/api/party/${TOKEN}/game/join`;
const VOTE = `/api/party/${TOKEN}/game/vote`;

function snapshot(over: Partial<PartyGamePublicSnapshot> = {}): PartyGamePublicSnapshot {
  return {
    albumName: 'Festa di Anna',
    status: 'live',
    phase: 'voting_open',
    version: 3,
    roundNumber: 1,
    totalChallenges: 4,
    phaseEndsAt: null,
    roundId: 'r1',
    myVote: null,
    voting: { received: 2, eligible: 5, yes: null, no: null, passed: null },
    challenge: {
      id: 'c1', title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare',
      mediaUrl: null, durationSeconds: null, votingMode: 'binary',
      voteQuestion: null,
    },
    ...over,
  };
}

// A poll firing on a timer updates React state, so the advance belongs inside
// act() — otherwise every ticking test prints a warning that means nothing.
async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

function mount() {
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${TOKEN}/game`]}>
        <Routes><Route path="/party/:token/game" element={<PartyGamePage />} /></Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

beforeEach(() => { vi.useFakeTimers({ shouldAdvanceTime: true }); });
afterEach(() => { vi.useRealTimers(); cleanup(); vi.unstubAllGlobals(); });

describe('the guest live game', () => {
  it('announces itself once with a POST, then only reads', async () => {
    const calls: string[] = [];
    installFetchMock({
      [`POST ${JOIN}`]: () => { calls.push('join'); return jsonResponse(snapshot({ status: 'lobby', phase: 'lobby' })); },
      [`GET ${READ}`]: () => { calls.push('read'); return jsonResponse(snapshot({ status: 'lobby', phase: 'lobby' })); },
    });
    mount();
    await screen.findByTestId('party-game-page');

    await advance(6_000);
    // Joining is what makes a phone count toward the room. It happens once; a
    // television would never do it at all, which is why reading mints nothing.
    expect(calls.filter((x) => x === 'join')).toHaveLength(1);
    expect(calls.filter((x) => x === 'read').length).toBeGreaterThan(0);
  });

  it('shows the lobby before the host starts', async () => {
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot({ status: 'lobby', phase: 'lobby', challenge: null, voting: null, roundId: null })),
      [`GET ${READ}`]: () => jsonResponse(snapshot({ status: 'lobby', phase: 'lobby', challenge: null, voting: null, roundId: null })),
    });
    mount();
    expect(await screen.findByText(/il gioco sta per iniziare/i)).toBeInTheDocument();
    expect(screen.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'lobby');
  });

  it('sends the guest to the screen while the activity is happening', async () => {
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot({ phase: 'challenge_active' })),
      [`GET ${READ}`]: () => jsonResponse(snapshot({ phase: 'challenge_active' })),
    });
    mount();
    expect(await screen.findByText(/guarda lo schermo/i)).toBeInTheDocument();
    // The activity is summarised, never re-performed on the phone.
    expect(screen.getByTestId('party-game-summary')).toHaveAttribute('data-mode', 'compact');
    expect(screen.queryByTestId('party-game-vote-yes')).not.toBeInTheDocument();
  });

  it('asks one question with two large answers and confirms the tap', async () => {
    let current = snapshot();
    const votes: unknown[] = [];
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(current),
      [`GET ${READ}`]: () => jsonResponse(current),
      [`POST ${VOTE}`]: ({ body }: { body: string | null }) => {
        votes.push(JSON.parse(body ?? '{}'));
        current = snapshot({ myVote: 'yes', voting: { received: 3, eligible: 5, yes: null, no: null, passed: null } });
        return jsonResponse(current);
      },
    });
    mount();

    const yes = await screen.findByTestId('party-game-vote-yes');
    expect(yes).toHaveTextContent(/superata/i);
    expect(screen.getByTestId('party-game-vote-no')).toHaveTextContent(/non superata/i);
    // The default question is used when the host wrote none.
    expect(screen.getByRole('heading', { name: /ha superato la sfida\?/i })).toBeInTheDocument();

    await userEvent.setup({ advanceTimers: vi.advanceTimersByTime }).click(yes);
    await waitFor(() => expect(votes).toHaveLength(1));
    // The vote names the round it answers, so a phone that fell behind cannot
    // land it on the next activity.
    expect(votes[0]).toEqual({ roundId: 'r1', value: 'yes' });

    expect(await screen.findByTestId('party-game-confirmed')).toHaveTextContent(/voto registrato/i);
    expect(screen.getByTestId('party-game-vote-yes')).toHaveAttribute('aria-pressed', 'true');
  });

  it('uses the question the host wrote when there is one', async () => {
    const withQuestion = snapshot({
      challenge: { ...snapshot().challenge!, voteQuestion: 'Ce l’ha fatta davvero?' },
    });
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(withQuestion),
      [`GET ${READ}`]: () => jsonResponse(withQuestion),
    });
    mount();
    expect(await screen.findByRole('heading', { name: /ce l’ha fatta davvero\?/i })).toBeInTheDocument();
  });

  it('lets a guest change their mind while voting is open', async () => {
    let current = snapshot({ myVote: 'yes' });
    const values: string[] = [];
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(current),
      [`GET ${READ}`]: () => jsonResponse(current),
      [`POST ${VOTE}`]: ({ body }: { body: string | null }) => {
        const value = JSON.parse(body ?? '{}').value as string;
        values.push(value);
        current = snapshot({ myVote: value as 'yes' | 'no' });
        return jsonResponse(current);
      },
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(await screen.findByTestId('party-game-vote-no'));
    await waitFor(() => expect(values).toEqual(['no']));
    await waitFor(() => expect(screen.getByTestId('party-game-vote-no')).toHaveAttribute('aria-pressed', 'true'));
    expect(screen.getByTestId('party-game-vote-yes')).toHaveAttribute('aria-pressed', 'false');
  });

  it('takes a refusal as the truth rather than as an error', async () => {
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
      // The host closed voting between the tap and its arrival. The refusal
      // carries the state it was measured against.
      [`POST ${VOTE}`]: () => errorResponse(409, {
        code: 'voting_closed',
        snapshot: snapshot({ phase: 'voting_closed', voting: { received: 3, eligible: 5, yes: null, no: null, passed: null } }),
      }),
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(await screen.findByTestId('party-game-vote-yes'));

    await waitFor(() =>
      expect(screen.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'waiting'));
    expect(screen.queryByRole('alert')).not.toBeInTheDocument();
  });

  it('says so when a tap arrives without an identity this party issued', async () => {
    // A vote never mints a voter, so a phone whose participant session is gone
    // is refused. Adopting the snapshot silently would leave a tap that did
    // nothing and said nothing.
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
      [`POST ${VOTE}`]: () => errorResponse(409, {
        code: 'not_joined', snapshot: snapshot(),
      }),
    });
    mount();
    const user = userEvent.setup({ advanceTimers: vi.advanceTimersByTime });
    await user.click(await screen.findByTestId('party-game-vote-yes'));

    expect(await screen.findByRole('alert')).toHaveTextContent(/il voto non è arrivato/i);
    expect(screen.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'vote');
  });

  it('never shows a result before it is revealed', async () => {
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot({ phase: 'voting_closed', myVote: 'yes' })),
      [`GET ${READ}`]: () => jsonResponse(snapshot({ phase: 'voting_closed', myVote: 'yes' })),
    });
    mount();
    expect(await screen.findByText(/votazione chiusa/i)).toBeInTheDocument();
    expect(screen.queryByTestId('party-game-percent')).not.toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/superata/i);
  });

  it('shows the revealed result, synchronised with everybody else', async () => {
    const revealed = snapshot({
      phase: 'result',
      voting: { received: 11, eligible: 12, yes: 9, no: 2, passed: true },
    });
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(revealed),
      [`GET ${READ}`]: () => jsonResponse(revealed),
    });
    mount();
    expect(await screen.findByTestId('party-game-percent')).toHaveTextContent('82%');
    expect(screen.getByRole('heading', { name: /sfida superata/i })).toBeInTheDocument();
  });

  it('says nothing about percentages for an activity nobody voted on', async () => {
    const noVote = snapshot({
      phase: 'result', voting: null,
      challenge: { ...snapshot().challenge!, votingMode: 'none' },
    });
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(noVote),
      [`GET ${READ}`]: () => jsonResponse(noVote),
    });
    mount();
    expect(await screen.findByText(/si passa alla prossima/i)).toBeInTheDocument();
    expect(screen.queryByTestId('party-game-percent')).not.toBeInTheDocument();
  });

  it('follows the game from one round to the next without being told to', async () => {
    let current = snapshot({ phase: 'voting_open' });
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(current),
      [`GET ${READ}`]: () => jsonResponse(current),
    });
    mount();
    await screen.findByTestId('party-game-vote-yes');

    // The host moves on. Nothing on the phone decides this; the next poll does.
    current = snapshot({ phase: 'challenge_reveal', roundNumber: 2, roundId: 'r2', voting: null });
    await advance(3_000);
    await waitFor(() =>
      expect(screen.getByTestId('party-game-page')).toHaveAttribute('data-scene', 'watch'));
  });

  it('keeps the scene on screen when a poll fails, and says it is behind', async () => {
    let fail = false;
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => (fail ? errorResponse(503) : jsonResponse(snapshot())),
    });
    mount();
    await screen.findByTestId('party-game-vote-yes');

    fail = true;
    await advance(3_000);
    // The party did not stop because one request did.
    await waitFor(() => expect(screen.getByRole('status')).toHaveTextContent(/riconnessione/i));
    expect(screen.getByTestId('party-game-vote-yes')).toBeInTheDocument();

    fail = false;
    await advance(3_000);
    await waitFor(() => expect(screen.queryByText(/riconnessione/i)).not.toBeInTheDocument());
  });

  it('says so plainly when this party has no game', async () => {
    installFetchMock({
      [`POST ${JOIN}`]: () => errorResponse(404),
      [`GET ${READ}`]: () => errorResponse(404),
    });
    mount();
    expect(await screen.findByText(/il gioco non è disponibile/i)).toBeInTheDocument();
  });

  it('carries no owner control anywhere on the page', async () => {
    installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
    });
    mount();
    await screen.findByTestId('party-game-vote-yes');
    const page = document.body.textContent ?? '';
    expect(page).not.toMatch(/avvia|apri votazione|chiudi votazione|prossima attività|termina/i);
    // Exactly two controls: the two answers. Plus the back link and language.
    expect(screen.getAllByRole('button').filter((b) => b.className.includes('party-game-answer')))
      .toHaveLength(2);
  });
});

describe('guestScene', () => {
  it('is a pure projection of the server phase — there is no second state machine', () => {
    expect(guestScene(null)).toBe('lobby');
    expect(guestScene(snapshot({ status: 'lobby', phase: 'lobby' }))).toBe('lobby');
    expect(guestScene(snapshot({ phase: 'challenge_reveal' }))).toBe('watch');
    expect(guestScene(snapshot({ phase: 'challenge_active' }))).toBe('watch');
    expect(guestScene(snapshot({ phase: 'voting_open' }))).toBe('vote');
    expect(guestScene(snapshot({ phase: 'voting_closed' }))).toBe('waiting');
    expect(guestScene(snapshot({ phase: 'result' }))).toBe('result');
    expect(guestScene(snapshot({ status: 'finished', phase: 'finished' }))).toBe('finished');
    // Finished wins over any phase: a game that is over is over.
    expect(guestScene(snapshot({ status: 'finished', phase: 'voting_open' }))).toBe('finished');
  });
});

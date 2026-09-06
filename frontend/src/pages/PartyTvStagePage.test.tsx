import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter, Route, Routes } from 'react-router';
import type { PartyGamePublicSnapshot } from '@nubarca/api-client';
import { I18nProvider } from '../i18n';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyTvStagePage, stageScene } from './PartyTvStagePage';
import { formatCountdown, secondsRemaining } from '../party/useCountdown';

vi.mock('qrcode', () => ({
  default: { toString: () => Promise.resolve('<svg data-testid="qr" />') },
}));

const TOKEN = 'tok-1';
const READ = `/api/party/${TOKEN}/game`;

function snapshot(over: Partial<PartyGamePublicSnapshot> = {}): PartyGamePublicSnapshot {
  return {
    albumName: 'Festa di Anna',
    status: 'live', phase: 'challenge_reveal', version: 2,
    roundNumber: 1, totalChallenges: 4, phaseEndsAt: null,
    roundId: 'r1', myVote: null, voting: null,
    challenge: {
      id: 'c1', title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare',
      mediaUrl: null, durationSeconds: null, votingMode: 'binary', voteQuestion: null,
    },
    ...over,
  };
}

async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

function mount() {
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/${TOKEN}/tv`]}>
        <Routes><Route path="/party/:token/tv" element={<PartyTvStagePage />} /></Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
}

function serve(get: () => PartyGamePublicSnapshot) {
  return installFetchMock({ [`GET ${READ}`]: () => jsonResponse(get()) });
}

beforeEach(() => { vi.useFakeTimers({ shouldAdvanceTime: true }); });
afterEach(() => { vi.useRealTimers(); cleanup(); vi.unstubAllGlobals(); });

describe('the television stage', () => {
  it('is a display: nothing on it can be pressed', async () => {
    let current = snapshot();
    const mock = serve(() => current);
    mount();
    await screen.findByTestId('party-stage-card');

    for (const phase of ['challenge_active', 'voting_open', 'voting_closed', 'result'] as const) {
      current = snapshot({
        phase,
        voting: { received: 8, eligible: 12, yes: phase === 'result' ? 9 : null, no: phase === 'result' ? 2 : null, passed: phase === 'result' ? true : null },
      });
      await advance(3_000);
      await waitFor(() => expect(screen.getByTestId('party-tv-stage')).toBeInTheDocument());
      expect(screen.queryAllByRole('button')).toHaveLength(0);
      expect(screen.queryAllByRole('link')).toHaveLength(0);
      expect(document.querySelectorAll('input, select, textarea, [tabindex]')).toHaveLength(0);
    }

    // And it never commands the game: only reads.
    expect(mock.calls.every((c) => c.method === 'GET')).toBe(true);
  });

  it('never mints a guest session', async () => {
    const mock = serve(snapshot);
    mount();
    await screen.findByTestId('party-stage-card');
    await advance(6_000);
    // A television that joined would inflate the very count it displays.
    expect(mock.calls.some((c) => c.url.includes('/join'))).toBe(false);
  });

  it('shows the way in while the game has not started', async () => {
    serve(() => snapshot({ status: 'lobby', phase: 'lobby', challenge: null, roundId: null }));
    mount();
    expect(await screen.findByText(/il gioco sta per iniziare/i)).toBeInTheDocument();
    expect(await screen.findByTestId('party-stage-qr')).toBeInTheDocument();
  });

  it('reveals the activity with THE canonical card', async () => {
    serve(snapshot);
    mount();
    const card = await screen.findByTestId('party-stage-card');
    expect(card).toHaveAttribute('data-mode', 'tv');
    expect(card).toHaveAttribute('data-kind', 'dare');
    expect(card).toHaveTextContent('Canta');
    expect(card).toHaveTextContent(/attività 1 di 4/i);
  });

  it('counts down only while the activity is running', async () => {
    const endsAt = new Date(Date.now() + 90_000).toISOString();
    let current = snapshot({ phase: 'challenge_reveal', phaseEndsAt: null });
    serve(() => current);
    mount();
    await screen.findByTestId('party-stage-card');
    expect(screen.queryByTestId('party-stage-timer')).not.toBeInTheDocument();

    current = snapshot({ phase: 'challenge_active', phaseEndsAt: endsAt });
    await advance(3_000);
    expect(await screen.findByTestId('party-stage-timer')).toHaveTextContent(/^1:2[0-9]$/);
  });

  it('calls for votes without saying which way they went', async () => {
    serve(() => snapshot({
      phase: 'voting_open',
      voting: { received: 8, eligible: 12, yes: null, no: null, passed: null },
    }));
    mount();
    expect(await screen.findByText(/vota ora/i)).toBeInTheDocument();
    const tally = screen.getByTestId('party-stage-tally');
    expect(tally).toHaveTextContent('8');
    expect(tally).toHaveTextContent('/ 12');
    // No percentage, no split, and no markup that could hold one.
    expect(screen.queryByTestId('party-stage-percent')).not.toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/%/);
  });

  it('holds the result back until the host reveals it', async () => {
    let current = snapshot({
      phase: 'voting_closed',
      voting: { received: 11, eligible: 12, yes: null, no: null, passed: null },
    });
    serve(() => current);
    mount();
    expect(await screen.findByText(/votazione chiusa/i)).toBeInTheDocument();
    expect(document.body.textContent).not.toMatch(/%|superata/i);

    current = snapshot({
      phase: 'result',
      voting: { received: 11, eligible: 12, yes: 9, no: 2, passed: true },
    });
    await advance(3_000);
    expect(await screen.findByTestId('party-stage-percent')).toHaveTextContent('82%');
    expect(screen.getByTestId('party-stage-verdict')).toHaveTextContent(/sfida superata/i);
    expect(screen.getByTestId('party-stage-verdict')).toHaveAttribute('data-passed', 'true');
  });

  it('says a challenge failed as a word, not only as a colour', async () => {
    serve(() => snapshot({
      phase: 'result',
      voting: { received: 10, eligible: 12, yes: 3, no: 7, passed: false },
    }));
    mount();
    expect(await screen.findByTestId('party-stage-verdict')).toHaveTextContent(/non superata/i);
    expect(screen.getByTestId('party-stage-percent')).toHaveTextContent('30%');
  });

  it('lands on the current scene after a reload, with no flourish for what the room already saw', async () => {
    // A television switched back on mid-reveal: the first snapshot is the
    // current one, and the between-rounds beat does not replay.
    serve(snapshot);
    mount();
    await screen.findByTestId('party-stage-card');
    expect(screen.queryByTestId('party-stage-intro')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'reveal');
  });

  it('plays the between-rounds beat on an observed change of round', async () => {
    let current = snapshot({ phase: 'result', roundId: 'r1' });
    serve(() => current);
    mount();
    await screen.findByText(/si passa alla prossima|il pubblico ha deciso/i);

    current = snapshot({ phase: 'challenge_reveal', roundId: 'r2', roundNumber: 2 });
    await advance(3_000);
    expect(await screen.findByTestId('party-stage-intro')).toHaveTextContent(/prossima attività/i);
    expect(screen.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'intro');

    // And it is a beat, not a mode: the activity follows on its own.
    await advance(2_500);
    await waitFor(() =>
      expect(screen.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'reveal'));
  });

  it('lets the snapshot end the beat early when the host moves on', async () => {
    let current = snapshot({ phase: 'result', roundId: 'r1' });
    serve(() => current);
    mount();
    await screen.findByTestId('party-tv-stage');

    current = snapshot({ phase: 'challenge_reveal', roundId: 'r2' });
    await advance(3_000);
    await screen.findByTestId('party-stage-intro');

    current = snapshot({ phase: 'challenge_active', roundId: 'r2' });
    await advance(3_000);
    await waitFor(() =>
      expect(screen.getByTestId('party-tv-stage')).toHaveAttribute('data-scene', 'active'));
  });

  it('closes the show', async () => {
    serve(() => snapshot({ status: 'finished', phase: 'finished', roundNumber: 4, challenge: null, roundId: null }));
    mount();
    expect(await screen.findByText(/grazie a tutti/i)).toBeInTheDocument();
    expect(screen.getByText(/dopo 4 attività/i)).toBeInTheDocument();
  });

  it('keeps the scene when the network goes and says so quietly', async () => {
    let fail = false;
    installFetchMock({
      [`GET ${READ}`]: () => (fail ? errorResponse(503) : jsonResponse(snapshot())),
    });
    mount();
    await screen.findByTestId('party-stage-card');
    fail = true;
    await advance(3_000);
    await waitFor(() => expect(screen.getByText(/riconnessione/i)).toBeInTheDocument());
    // The activity is still on screen: a television that lost Wi-Fi has not
    // stopped the party.
    expect(screen.getByTestId('party-stage-card')).toBeInTheDocument();
  });

  it('says plainly when this party has no game', async () => {
    installFetchMock({ [`GET ${READ}`]: () => errorResponse(404) });
    mount();
    expect(await screen.findByText(/nessun gioco/i)).toBeInTheDocument();
  });
});

describe('stageScene', () => {
  it('is a pure projection of the server phase', () => {
    expect(stageScene(null)).toBe('lobby');
    expect(stageScene(snapshot({ status: 'lobby', phase: 'lobby' }))).toBe('lobby');
    expect(stageScene(snapshot({ phase: 'challenge_reveal' }))).toBe('reveal');
    expect(stageScene(snapshot({ phase: 'challenge_active' }))).toBe('active');
    expect(stageScene(snapshot({ phase: 'voting_open' }))).toBe('vote');
    expect(stageScene(snapshot({ phase: 'voting_closed' }))).toBe('closed');
    expect(stageScene(snapshot({ phase: 'result' }))).toBe('result');
    expect(stageScene(snapshot({ status: 'finished', phase: 'voting_open' }))).toBe('finished');
  });
});

describe('the activity clock', () => {
  it('counts whole seconds and never goes negative', () => {
    const now = Date.parse('2026-09-06T20:00:00Z');
    expect(secondsRemaining('2026-09-06T20:02:00Z', now)).toBe(120);
    expect(secondsRemaining('2026-09-06T20:00:00.400Z', now)).toBe(1);
    expect(secondsRemaining('2026-09-06T19:59:00Z', now)).toBe(0);
    expect(secondsRemaining(null, now)).toBeNull();
    expect(secondsRemaining('not a date', now)).toBeNull();
  });

  it('reads as a clock', () => {
    expect(formatCountdown(0)).toBe('0:00');
    expect(formatCountdown(9)).toBe('0:09');
    expect(formatCountdown(90)).toBe('1:30');
    expect(formatCountdown(3599)).toBe('59:59');
  });
});

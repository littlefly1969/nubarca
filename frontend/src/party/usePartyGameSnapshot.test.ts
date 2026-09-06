import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import type { PartyGamePublicSnapshot } from '@nubarca/api-client';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { usePartyGameSnapshot } from './usePartyGameSnapshot';

const TOKEN = 'tok-1';
const READ = `/api/party/${TOKEN}/game`;
const JOIN = `/api/party/${TOKEN}/game/join`;

function snapshot(): PartyGamePublicSnapshot {
  return {
    albumName: 'Festa', status: 'live', phase: 'voting_open', version: 3,
    roundNumber: 1, totalChallenges: 2, phaseEndsAt: null, roundId: 'r1',
    myVote: null, voting: { received: 1, eligible: 4, yes: null, no: null, passed: null },
    challenge: {
      id: 'c1', title: 'Canta', body: 'Sali sul tavolo.', kind: 'dare',
      mediaUrl: null, durationSeconds: null, votingMode: 'binary', voteQuestion: null,
    },
  };
}

async function advance(ms: number) {
  await act(async () => { await vi.advanceTimersByTimeAsync(ms); });
}

const joins = (calls: { url: string; method: string }[]) =>
  calls.filter((c) => c.method === 'POST' && c.url.endsWith('/game/join'));
const reads = (calls: { url: string; method: string }[]) =>
  calls.filter((c) => c.method === 'GET' && c.url.includes('/game'));

beforeEach(() => { vi.useFakeTimers({ shouldAdvanceTime: true }); });
afterEach(() => { vi.useRealTimers(); cleanup(); vi.unstubAllGlobals(); });

describe('the guest feed', () => {
  it('never launches a second join while the first is still unresolved', async () => {
    // A phone on a slow network. Without an in-flight guard the interval is a
    // request generator: every tick joins again, and every join mints a
    // participant that is counted in the room and can vote.
    const mock = installFetchMock({
      [`POST ${JOIN}`]: () => new Promise<Response>(() => {}),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
    });
    renderHook(() => usePartyGameSnapshot(TOKEN, { join: true }));

    await advance(12_000); // four poll periods
    expect(joins(mock.calls)).toHaveLength(1);
    // And nothing fell through to a plain read either: the join is still the
    // outstanding request.
    expect(reads(mock.calls)).toHaveLength(0);
  });

  it('retries the join when the first one fails', async () => {
    let fail = true;
    const mock = installFetchMock({
      [`POST ${JOIN}`]: () => (fail ? errorResponse(503) : jsonResponse(snapshot())),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
    });
    renderHook(() => usePartyGameSnapshot(TOKEN, { join: true }));

    await advance(3_000);
    expect(joins(mock.calls).length).toBeGreaterThanOrEqual(2);
    // A failure must not be silently downgraded to a poll: the guest is still
    // not in the room, so the next attempt is still a join.
    expect(reads(mock.calls)).toHaveLength(0);

    // Once one succeeds the guest is in the room, and the feed settles into
    // polling.
    fail = false;
    await advance(9_000);
    expect(reads(mock.calls).length).toBeGreaterThan(0);
    const joinsAfterSuccess = joins(mock.calls).length;
    await advance(9_000);
    expect(joins(mock.calls)).toHaveLength(joinsAfterSuccess);
  });

  it('stops joining once one has succeeded, and only polls after that', async () => {
    const mock = installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
    });
    renderHook(() => usePartyGameSnapshot(TOKEN, { join: true }));

    await advance(12_000);
    expect(joins(mock.calls)).toHaveLength(1);
    expect(reads(mock.calls).length).toBeGreaterThanOrEqual(3);
  });

  it('cannot be made to overlap by waking events', async () => {
    // visibilitychange, focus and online all read immediately. Fired together
    // while a join is outstanding, they must add nothing.
    const mock = installFetchMock({
      [`POST ${JOIN}`]: () => new Promise<Response>(() => {}),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
    });
    renderHook(() => usePartyGameSnapshot(TOKEN, { join: true }));
    await advance(100);

    await act(async () => {
      for (let i = 0; i < 5; i += 1) {
        document.dispatchEvent(new Event('visibilitychange'));
        window.dispatchEvent(new Event('focus'));
        window.dispatchEvent(new Event('online'));
      }
    });
    await advance(6_000);

    expect(joins(mock.calls)).toHaveLength(1);
  });

  it('joins again for a different party, and not for the same one', async () => {
    const mock = installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
      'POST /api/party/tok-2/game/join': () => jsonResponse(snapshot()),
      'GET /api/party/tok-2/game': () => jsonResponse(snapshot()),
    });
    const { rerender } = renderHook(
      ({ token }: { token: string }) => usePartyGameSnapshot(token, { join: true }),
      { initialProps: { token: TOKEN } },
    );
    await advance(3_000);
    expect(joins(mock.calls)).toHaveLength(1);

    // A new token is a new party: the guard resets rather than carrying the
    // previous party's "already joined" over to it.
    rerender({ token: 'tok-2' });
    await advance(3_000);
    const all = joins(mock.calls);
    expect(all).toHaveLength(2);
    expect(all[1].url).toContain('tok-2');
  });

  it('does not join at all for a television', async () => {
    const mock = installFetchMock({
      [`POST ${JOIN}`]: () => jsonResponse(snapshot()),
      [`GET ${READ}`]: () => jsonResponse(snapshot()),
    });
    renderHook(() => usePartyGameSnapshot(TOKEN, { join: false, asDisplay: true }));

    await advance(9_000);
    expect(joins(mock.calls)).toHaveLength(0);
    // And it says it is a display, which is what lets the control room answer
    // "is a screen showing this".
    expect(reads(mock.calls).every((c) => c.url.includes('display=1'))).toBe(true);
  });
});

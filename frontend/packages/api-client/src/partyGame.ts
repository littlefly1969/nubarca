import { api, ApiError } from './client';
import type { PartyChallengeKind, PartyChallengeVotingMode } from './party';

// The live Party Game, as the three surfaces that watch it see it.
//
// There is no realtime transport here and there is none anywhere in NubArca:
// every live surface polls a snapshot and compares `version`. A client learns
// that something changed by seeing a higher one, and recovers from a refresh, a
// backgrounded tab or a dropped network by reading the snapshot again — which is
// the entire reconnection story.

export type PartyGameStatus = 'lobby' | 'live' | 'finished';

export type PartyGamePhase =
  | 'lobby'
  | 'challenge_reveal'
  | 'challenge_active'
  | 'voting_open'
  | 'voting_closed'
  | 'result'
  | 'finished';

export type PartyGameCommand =
  | 'start'
  | 'start_challenge'
  | 'open_voting'
  | 'close_voting'
  | 'reveal_result'
  | 'next_challenge'
  | 'skip_challenge'
  | 'finish';

export type PartyGameVoteValue = 'yes' | 'no';

/**
 * What the room has said. `yes` / `no` / `passed` are null until the audience
 * being served may know — the owner from the moment voting closes, a guest or a
 * television only once it is revealed.
 */
export interface PartyGameVoting {
  received: number;
  eligible: number;
  yes: number | null;
  no: number | null;
  passed: boolean | null;
}

export interface PartyGameChallenge {
  id: string;
  title: string;
  body: string;
  kind: PartyChallengeKind;
  mediaUrl: string | null;
  durationSeconds: number | null;
  votingMode: PartyChallengeVotingMode;
  voteQuestion: string | null;
}

/** The owner's complete view. Everything a control room renders comes from one. */
export interface PartyGameSnapshot {
  albumId: string;
  sessionId: string | null;
  status: PartyGameStatus;
  phase: PartyGamePhase;
  /** The optimistic-concurrency token every command must quote. 0 = not started. */
  version: number;
  roundNumber: number;
  totalChallenges: number;
  playedRounds: number;
  startedAt: string | null;
  finishedAt: string | null;
  phaseStartedAt: string | null;
  phaseEndsAt: string | null;
  currentChallenge: PartyGameChallenge | null;
  nextChallenge: PartyGameChallenge | null;
  /**
   * What the server says is legal right now, so a control room can OMIT an
   * illegal command rather than disable it — without a second copy of the state
   * machine here. Advisory: every command is validated again on arrival.
   */
  availableCommands: PartyGameCommand[];
  voting: PartyGameVoting | null;
}

/** What a guest phone or a television is told. A strict subset. */
export interface PartyGamePublicSnapshot {
  albumName: string;
  status: PartyGameStatus;
  phase: PartyGamePhase;
  version: number;
  roundNumber: number;
  totalChallenges: number;
  phaseEndsAt: string | null;
  challenge: Omit<PartyGameChallenge, 'mediaUrl'> & { mediaUrl: string | null } | null;
  /** What a vote would be about. Stable for a whole round, unlike the version. */
  roundId: string | null;
  voting: PartyGameVoting | null;
  /** This caller's own answer. Always null for a television: it holds no session. */
  myVote: PartyGameVoteValue | null;
}

export type PartyGameCommandCode =
  | 'game_disabled' | 'version_conflict' | 'illegal_transition' | 'no_challenges' | 'conflict';

export type PartyGameVoteCode = 'voting_closed' | 'stale_round' | 'conflict';

/**
 * A refusal carries the state it was measured against, so a caller that fell
 * behind ends the request CORRECT rather than merely told off — and a double tap
 * becomes one advance plus a re-render instead of a second request.
 */
export class PartyGameConflict<TCode extends string, TSnapshot> extends Error {
  constructor(readonly code: TCode, readonly snapshot: TSnapshot | null) {
    super(code);
    this.name = 'PartyGameConflict';
  }
}

export type PartyGameCommandConflict = PartyGameConflict<PartyGameCommandCode, PartyGameSnapshot>;
export type PartyGameVoteConflict = PartyGameConflict<PartyGameVoteCode, PartyGamePublicSnapshot>;

// --- Owner ----------------------------------------------------------------

export function getPartyGameSnapshot(
  albumId: string, signal?: AbortSignal,
): Promise<PartyGameSnapshot> {
  return api<PartyGameSnapshot>(`/api/albums/${albumId}/party-game`, { signal });
}

/**
 * Sends one command, quoting the version it believes it is acting on. A stale
 * or illegal command throws a PartyGameConflict carrying the current snapshot.
 */
export async function sendPartyGameCommand(
  albumId: string, command: PartyGameCommand, expectedVersion: number, signal?: AbortSignal,
): Promise<PartyGameSnapshot> {
  try {
    return await api<PartyGameSnapshot>(`/api/albums/${albumId}/party-game/commands`, {
      method: 'POST', json: { command, expectedVersion }, signal,
    });
  } catch (error) {
    throw asConflict<PartyGameCommandCode, PartyGameSnapshot>(error);
  }
}

// --- Guest / television ---------------------------------------------------

export function getPartyGamePublicSnapshot(
  token: string, signal?: AbortSignal,
): Promise<PartyGamePublicSnapshot> {
  return api<PartyGamePublicSnapshot>(
    `/api/party/${encodeURIComponent(token)}/game`, { signal });
}

/**
 * "I am here." Mints the guest's participant session, so a phone counts toward
 * the room from the moment it opens the game rather than from its first vote.
 * A television never calls this, which is why the read above mints nothing.
 */
export function joinPartyGame(
  token: string, signal?: AbortSignal,
): Promise<PartyGamePublicSnapshot> {
  return api<PartyGamePublicSnapshot>(
    `/api/party/${encodeURIComponent(token)}/game/join`, { method: 'POST', signal });
}

export async function submitPartyGameVote(
  token: string, roundId: string, value: PartyGameVoteValue, signal?: AbortSignal,
): Promise<PartyGamePublicSnapshot> {
  try {
    return await api<PartyGamePublicSnapshot>(
      `/api/party/${encodeURIComponent(token)}/game/vote`,
      { method: 'POST', json: { roundId, value }, signal });
  } catch (error) {
    throw asConflict<PartyGameVoteCode, PartyGamePublicSnapshot>(error);
  }
}

/**
 * The share of the room that said yes, as a whole percentage.
 *
 * Here rather than in each surface because a television, a phone and a control
 * room showing a party three different numbers for the same vote would be worse
 * than showing none. Null when there is nothing to divide.
 */
export function partyGameYesPercent(voting: PartyGameVoting | null | undefined): number | null {
  if (!voting || voting.yes === null || voting.no === null) return null;
  const cast = voting.yes + voting.no;
  return cast === 0 ? null : Math.round((voting.yes / cast) * 100);
}

function asConflict<TCode extends string, TSnapshot>(error: unknown): unknown {
  if (error instanceof ApiError && error.status === 409) {
    const body = error.body as { code?: TCode; snapshot?: TSnapshot } | null;
    return new PartyGameConflict<TCode, TSnapshot>(
      body?.code ?? ('conflict' as TCode), body?.snapshot ?? null);
  }
  return error;
}

import { api, ApiError } from './client';
import type {
  PartyChallengeKind, PartyChallengeOption, PartyChallengeVotingMode,
} from './party';

// The live Party Game, as the three surfaces that watch it see it.
//
// There is no realtime transport here and there is none anywhere in NubArca:
// every live surface polls the authoritative snapshot. Every successful poll is
// the current truth and is rendered as such, and a refresh, a backgrounded tab
// or a dropped network all recover the same way — by reading it again. That is
// the entire reconnection story.
//
// `version` is NOT a change feed, and a client must never skip a response
// because it did not move. It is the OWNER's optimistic-concurrency token: it
// changes when an owner command changes command-authoritative game state, and
// deliberately not when a guest votes — a vote changes participation and,
// later, the result, but bumping the token would make the host's next command
// fail as stale.

export type PartyGameStatus = 'lobby' | 'live' | 'finished';

export type PartyGamePhase =
  | 'lobby'
  | 'challenge_reveal'
  | 'challenge_active'
  | 'voting_open'
  | 'voting_closed'
  | 'result'
  // The game is alive and nothing is being played: the host sent the room back
  // to the party between two activities. It is NOT `finished` — every round
  // played, the host's plan and the guests' preferences all survive it, and
  // `next_challenge` resumes. The television returns to the party slideshow
  // because the server's presentation projection says so.
  | 'intermission'
  | 'finished';

export type PartyGameCommand =
  | 'start'
  | 'start_challenge'
  | 'open_voting'
  | 'close_voting'
  | 'reveal_result'
  | 'next_challenge'
  | 'skip_challenge'
  | 'finish'
  // Play the same party again. Legal only from `finished`, and the only command
  // that discards rather than advances: the finished match's rounds and votes
  // go, while the link, its token, the guests and everything they contributed
  // stay. The version still moves FORWARD, so a command written during the game
  // that just ended remains stale.
  | 'restart_game'
  // Give the room back to the party between two activities. Legal only from
  // `result`, because it RESOLVES the round the room just saw the outcome of.
  | 'return_to_party';

/**
 * The two answers a VERDICT round has.
 *
 * A choice round is answered with the id of one of the activity's own options
 * instead, so anything that carries "whatever this guest answered" — `myVote`,
 * what the vote endpoint takes — is a plain string, and the server decides
 * which strings are answers to which question.
 */
export type PartyGameVoteValue = 'yes' | 'no';

/** A verdict, or the id of a choice round's option. */
export type PartyGameAnswer = PartyGameVoteValue | (string & {});

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
  /**
   * The result of a `choice` round, per answer. Null while voting is open, for
   * the same reason `yes`/`no` are: the room learns the split when the host
   * decides to reveal it, not before.
   */
  options?: PartyGameOptionResult[] | null;
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
  /** The answers, for a `choice` round. Null for every other mode. */
  options?: PartyChallengeOption[] | null;
}

/**
 * One answer's share of the room, once the host has closed voting.
 *
 * Counts and not a percentage, so two surfaces cannot round one party's verdict
 * differently — a caller divides by `received` itself. `winning` is decided by
 * the server because a tie is a product question with one answer: the first
 * answer the host wrote takes it.
 */
export interface PartyGameOptionResult {
  id: string;
  label: string;
  outcome: string | null;
  votes: number;
  winning: boolean;
}

/** Where one activity stands in the match, as the control room reads it. */
export type PartyGamePlanState = 'played' | 'current' | 'remaining';

/**
 * One activity as the host PLANNING the evening sees it.
 *
 * `state` is the whole authority over what may be edited: `played` and
 * `current` are the past and the present, and the control room offers no
 * control over either. `position` is 1-based among the activities the game
 * would actually play, and null for everything else — an excluded or
 * switched-off activity has no turn to number.
 *
 * `preferenceVotes` is what the room asked for BEFORE the match. It is carried
 * for every entry including an excluded one: a host setting something aside
 * should still see what they are setting aside.
 */
export interface PartyGamePlanEntry {
  id: string;
  title: string;
  mediaUrl: string | null;
  state: PartyGamePlanState;
  position: number | null;
  isEnabled: boolean;
  excluded: boolean;
  preferenceVotes: number;
}

/** Move a remaining activity, or decide it is not being played tonight. */
export type PartyGamePlanAction = 'move' | 'exclude' | 'include';

/** The owner's complete view. Everything a control room renders comes from one. */
export interface PartyGameSnapshot {
  albumId: string;
  sessionId: string | null;
  status: PartyGameStatus;
  phase: PartyGamePhase;
  /**
   * The optimistic-concurrency token every owner command must quote. 0 = not
   * started. Not a revision of the snapshot: votes change what this object says
   * without changing this number.
   */
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
  /**
   * The whole deck in play order with its state, the host's exclusions and the
   * preferences the room cast. Always present — empty for an empty deck — so a
   * control room never renders the evening from two reads that can disagree.
   */
  plan: PartyGamePlanEntry[];
  /** Whether the host asked the room which activities it would like to see. */
  priorityVotingEnabled: boolean;
  /** Whether the guests may still change their preferences. */
  preferencesOpen: boolean;
  /** Guests seen recently on this party link. */
  guestsPresent: number;
  /**
   * How long ago a screen last read the game, in seconds; null if none ever has.
   * Server-computed on purpose — a control room on a laptop with a drifting
   * clock must not decide for itself that the television has been dead an hour.
   */
  displaySeenSecondsAgo: number | null;
  tvUrl: string | null;
  guestUrl: string | null;
}

export type PartyGameDisplayState = 'connected' | 'stale' | 'disconnected';

/**
 * Whether a screen is showing this game.
 *
 * Pure, and here rather than in the page, because "connected" is a product
 * judgement about a polling interval: the stage reads every 2.5s, so a gap of
 * more than a few of those is a screen that has stopped, and a gap of minutes is
 * one that has gone.
 */
export function partyGameDisplayState(secondsAgo: number | null): PartyGameDisplayState {
  if (secondsAgo === null) return 'disconnected';
  if (secondsAgo <= 15) return 'connected';
  if (secondsAgo <= 120) return 'stale';
  return 'disconnected';
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
  myVote: PartyGameAnswer | null;
  /**
   * The pre-game preference surface, or null when the host did not ask the
   * room. It travels WITH the snapshot because a phone in the lobby needs both
   * halves — what the game is doing, and what it may still choose.
   */
  preferences: PartyGamePreferences | null;
}

/**
 * One activity a guest may say they would like to see.
 *
 * It deliberately carries NO vote count: a guest choosing must not be told what
 * everybody else picked first. The counts exist to inform the HOST's planning,
 * and the control room is where they are shown.
 */
export interface PartyGamePreferenceItem {
  id: string;
  title: string;
  body: string;
  mediaUrl: string | null;
  selected: boolean;
}

/**
 * The guest's pre-game preferences.
 *
 * They are ADVISORY. They choose no activity, interrupt no slideshow, enter no
 * yes/no result and move no phase — the host reads them and decides. `open`
 * closes at the first `start` and reopens on `restart_game`, and nothing is
 * ever deleted: a replay starts from what the room already said.
 */
export interface PartyGamePreferences {
  open: boolean;
  votesPerGuest: number;
  votesUsed: number;
  votesRemaining: number;
  items: PartyGamePreferenceItem[];
}

export type PartyGameCommandCode =
  | 'game_disabled' | 'version_conflict' | 'illegal_transition' | 'no_challenges'
  // A planning action that cannot be carried out: an unknown activity, or one
  // the host may not move — anything already played, and whatever is on screen.
  | 'invalid_plan'
  | 'conflict';

export type PartyGamePreferenceCode =
  | 'preferences_closed' | 'not_joined' | 'unknown_challenge' | 'limit_reached' | 'conflict';

export type PartyGameVoteCode =
  | 'voting_closed'
  | 'stale_round'
  // The caller holds no identity this party issued. A vote never mints one —
  // only `joinPartyGame` does — so this is a tap from a phone whose participant
  // session is gone, and it is recoverable by joining again.
  | 'not_joined'
  | 'conflict';

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
export type PartyGamePreferenceConflict =
  PartyGameConflict<PartyGamePreferenceCode, PartyGamePreferences>;

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

/**
 * One edit to the plan. Quotes a version like every other owner write, because
 * the order the game will walk is authoritative state and two hosts reordering
 * one deck from two phones must not silently overwrite each other.
 *
 * `position` is 0-based among the activities still to come, and is meaningful
 * only for `move`; 0 is "play this next".
 */
export async function planPartyGame(
  albumId: string,
  action: PartyGamePlanAction,
  challengeId: string,
  expectedVersion: number,
  position?: number,
  signal?: AbortSignal,
): Promise<PartyGameSnapshot> {
  try {
    return await api<PartyGameSnapshot>(`/api/albums/${albumId}/party-game/plan`, {
      method: 'POST',
      json: { action, challengeId, expectedVersion, position: position ?? null },
      signal,
    });
  } catch (error) {
    throw asConflict<PartyGameCommandCode, PartyGameSnapshot>(error);
  }
}

// --- Guest / television ---------------------------------------------------

/**
 * @param asDisplay a television saying so, which is what lets the control room
 * answer "is a screen showing this" honestly. It grants nothing.
 */
export function getPartyGamePublicSnapshot(
  token: string, signal?: AbortSignal, asDisplay = false,
): Promise<PartyGamePublicSnapshot> {
  return api<PartyGamePublicSnapshot>(
    `/api/party/${encodeURIComponent(token)}/game${asDisplay ? '?display=1' : ''}`, { signal });
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

/**
 * Adds or removes one pre-game preference.
 *
 * A different call from the vote because it is a different feature: this one
 * names an ACTIVITY and says "I would like to see this"; the vote names a ROUND
 * and says "they did it". They share the anonymous participant and nothing
 * else. The response is the whole preference surface, so a phone re-renders
 * from one answer.
 */
export async function setPartyGamePreference(
  token: string, challengeId: string, selected: boolean, signal?: AbortSignal,
): Promise<PartyGamePreferences> {
  try {
    const body = await api<PartyGamePreferences>(
      `/api/party/${encodeURIComponent(token)}/game/preferences`,
      { method: 'POST', json: { challengeId, selected }, signal });
    return body;
  } catch (error) {
    throw asPreferenceConflict(error);
  }
}

function asPreferenceConflict(error: unknown): unknown {
  if (error instanceof ApiError && error.status === 409) {
    const body = error.body as
      { code?: PartyGamePreferenceCode; preferences?: PartyGamePreferences } | null;
    return new PartyGameConflict<PartyGamePreferenceCode, PartyGamePreferences>(
      body?.code ?? 'conflict', body?.preferences ?? null);
  }
  return error;
}

export async function submitPartyGameVote(
  token: string, roundId: string, value: PartyGameAnswer, signal?: AbortSignal,
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

/**
 * The winning answer of a `choice` round, with its share of the room.
 *
 * Null while voting is open, and null when nobody voted — "everyone abstained"
 * is not a verdict, and a television must not announce one. The percentage is
 * computed HERE, from the counts the server sent, for the same reason the
 * server sends counts: one place rounds it, so one party sees one number.
 */
export function partyGameWinningOption(
  voting: PartyGameVoting | null | undefined,
): { option: PartyGameOptionResult; percent: number } | null {
  const options = voting?.options;
  if (!options || options.length === 0 || !voting) return null;
  const cast = options.reduce((total, o) => total + o.votes, 0);
  if (cast === 0) return null;
  const option = options.find((o) => o.winning);
  return option ? { option, percent: Math.round((option.votes / cast) * 100) } : null;
}

function asConflict<TCode extends string, TSnapshot>(error: unknown): unknown {
  if (error instanceof ApiError && error.status === 409) {
    const body = error.body as { code?: TCode; snapshot?: TSnapshot } | null;
    return new PartyGameConflict<TCode, TSnapshot>(
      body?.code ?? ('conflict' as TCode), body?.snapshot ?? null);
  }
  return error;
}

// --- Display surface ------------------------------------------------------
//
// A paired NubArca TV showing an assigned party. It is NOT a guest: it holds
// no party token and no browser cookie, only a display grant its native shell
// minted from the device's own session. The grant travels in a header, never a
// query string, because a URL reaches access logs, history and referrers.

/** The header a display presents. Matches PartyDisplayService.GrantHeader. */
export const PARTY_DISPLAY_GRANT_HEADER = 'X-Party-Display-Grant';

export function getPartyDisplaySnapshot(
  grant: string, signal?: AbortSignal,
): Promise<PartyGamePublicSnapshot> {
  return api<PartyGamePublicSnapshot>('/api/party-display/game', {
    headers: { [PARTY_DISPLAY_GRANT_HEADER]: grant },
    signal,
  });
}

/**
 * The lobby's join code, as PIXELS.
 *
 * The display cannot build this itself and must not be able to: the code
 * encodes the party's join URL, and holding that URL would make a television a
 * guest. The server encodes it instead, so the room can scan what the screen
 * shows while the screen holds nothing it could use.
 */
export async function getPartyDisplayJoinQr(
  grant: string, signal?: AbortSignal,
): Promise<string> {
  const response = await fetch('/api/party-display/join-qr', {
    headers: { [PARTY_DISPLAY_GRANT_HEADER]: grant },
    signal,
  });
  if (!response.ok) throw new ApiError(response.status, 'display join qr', null);
  return response.text();
}

/**
 * An activity photograph, fetched with the grant and handed back as an object
 * URL.
 *
 * An <img> cannot carry a header, and the alternatives — a credential in the
 * query string, or a guest token — are the two things this surface exists to
 * avoid. So the bytes are fetched properly and the DOM is given a blob: URL.
 * The caller owns it and must revoke it.
 */
export async function fetchPartyDisplayMedia(
  path: string, grant: string, signal?: AbortSignal,
): Promise<string> {
  const response = await fetch(path, {
    headers: { [PARTY_DISPLAY_GRANT_HEADER]: grant },
    signal,
  });
  if (!response.ok) throw new ApiError(response.status, 'display media', null);
  return URL.createObjectURL(await response.blob());
}

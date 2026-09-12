// What the server says this television should be showing, in the one shape the
// shell navigates by. Pure, node-testable.
//
// The server sends the ASSIGNMENT (general, or one party) and, beside it, the
// PRESENTATION that party wants right now — its slideshow, its game, or an
// honest "unavailable". The shell does not know what a game phase is and never
// will: it is told which of four surfaces to mount, and the four words are the
// whole vocabulary.

import type { TvDisplayAssignment } from '../api/tv';

export type PartyPresentation = 'slideshow' | 'game' | 'unavailable';

export interface AssignedParty {
  /**
   * The server's opaque identity for "this television, this party link". A new
   * value is a DIFFERENT party — including a new link for the same album — and
   * is what forces a teardown and a fresh capability.
   */
  readonly key: string;
  readonly albumId: string | null;
  readonly albumName: string | null;
}

export type AssignmentView =
  | { readonly presentation: 'general' }
  | { readonly presentation: PartyPresentation; readonly party: AssignedParty };

/**
 * How often a foreground, paired television reads the control plane.
 *
 * It is the bound on a takeover: an owner assigning a party, a game starting,
 * finishing or restarting all reach the screen within one interval. It is a
 * READ — `GET /api/tv/session` writes nothing — so it can afford to be brisk.
 */
export const CONTROL_POLL_MS = 5_000;

/**
 * How often one of those reads is the session HEARTBEAT instead.
 *
 * The heartbeat is what keeps `LastSeenAt` honest in the owner's device list,
 * and it is a write; there is no reason to write every five seconds to say "still
 * here". Same endpoint shape, same answer, a different verb once a minute.
 */
export const SESSION_HEARTBEAT_MS = 60_000;

/** Should THIS control-plane read be the heartbeat? */
export function shouldHeartbeat(lastHeartbeatAt: number | null, now: number): boolean {
  return lastHeartbeatAt === null || now - lastHeartbeatAt >= SESSION_HEARTBEAT_MS;
}

export const BACKOFF_BASE_MS = 2_000;
export const BACKOFF_MAX_MS = 30_000;

/**
 * The wait before retrying a read that failed for a reason that is not an
 * answer — no network, a 5xx. Exponential and capped, so a server that is down
 * for an hour is asked twice a minute rather than in a loop.
 *
 * Used where the shell must keep trying on its own: validating the session at
 * boot (a television that powers on before its Wi-Fi must not UNPAIR itself),
 * and loading the assigned party's slideshow.
 */
export function backoffMs(attempt: number): number {
  const steps = Math.max(0, Math.min(attempt, 10));
  return Math.min(BACKOFF_MAX_MS, BACKOFF_BASE_MS * 2 ** steps);
}

function isPartyPresentation(value: unknown): value is PartyPresentation {
  return value === 'slideshow' || value === 'game' || value === 'unavailable';
}

/**
 * The server's assignment DTO, as a navigation decision.
 *
 * A server that predates `presentation` is read by its own previous contract:
 * an available party assignment meant "stage the game", an unavailable one
 * meant "that party is over". Nothing about a newer shell may make an older
 * server's television show something it would not have shown before.
 */
export function toAssignmentView(dto: TvDisplayAssignment | null | undefined): AssignmentView {
  if (!dto || dto.kind !== 'party') return { presentation: 'general' };

  let presentation: PartyPresentation = isPartyPresentation(dto.presentation)
    ? dto.presentation
    : (dto.partyAvailable ? 'game' : 'unavailable');
  // A slideshow needs an album to show. The server never sends one without it;
  // if it ever did, failing closed is the only safe reading.
  if (presentation === 'slideshow' && !dto.albumId) presentation = 'unavailable';

  return {
    presentation,
    party: {
      key: dto.assignmentKey ?? `album:${dto.albumId ?? 'none'}`,
      albumId: dto.albumId ?? null,
      albumName: dto.albumName ?? null,
    },
  };
}

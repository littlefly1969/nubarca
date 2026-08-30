import type { VideoPlayerStatus } from 'expo-video';
import type { VideoProbeOutcome } from '../media/videoProbe.ts';

export type VideoProbeState =
  | 'idle'
  | 'probing'
  | 'ready'
  | 'preparing'
  | 'unavailable'
  | 'error';
export type VideoPresentation =
  | 'probing'
  | 'preparing'
  | 'unavailable'
  | 'loading'
  | 'ready'
  | 'error';

export interface PlayerStatusSource {
  readonly status: VideoPlayerStatus;
}

export interface PlayerStatusSnapshot {
  player: PlayerStatusSource;
  status: VideoPlayerStatus;
}

/**
 * The MOBILE presentation of a canonical delivery verdict.
 *
 * The transport verdicts stay distinct (VIDEO-DELIVERY-PARITY-01); what this
 * mapping decides is only how each one is DRAWN, and that is allowed to differ
 * per consumer:
 *   not-found / protocol-error → "unavailable": nothing to retry, so the slide
 *     shows the poster and says so rather than offering a pointless button.
 *   auth-error → "error": retrying is genuinely useful here, because the retry
 *     path re-reads the LIVE cookie jar (refreshVideoSourceCookie), so a
 *     session renewed mid-viewer recovers on one tap.
 *   transient-error → "error": the shared bounded retry is already exhausted
 *     by the time this arrives, so the user gets the retry action.
 *   cancelled → null: an unmount is not a verdict. It must never reach state.
 */
export function probeStateForOutcome(outcome: VideoProbeOutcome): VideoProbeState | null {
  switch (outcome.kind) {
    case 'ready':
      return 'ready';
    case 'preparing':
      return 'preparing';
    case 'not-found':
    case 'protocol-error':
      return 'unavailable';
    case 'auth-error':
    case 'transient-error':
      return 'error';
    case 'cancelled':
      return null;
  }
}

export interface AuthenticatedVideoSource {
  uri: string;
  headers: { cookie: string };
}

/** Keep the server-authorized URL intact while renewing the manual owner
 * cookie used by React Native. The viewer sequence may remain mounted long
 * enough for ASP.NET cookie renewal; replay must use the live jar rather than
 * the snapshot taken when the grid first opened. */
export function refreshVideoSourceCookie<T extends AuthenticatedVideoSource>(
  source: T | null,
  cookie: string | null,
): T | null {
  if (source === null || cookie === null || cookie.length === 0) return null;
  if (source.headers.cookie === cookie) return source;
  return {
    ...source,
    headers: { cookie },
  };
}

export function snapshotPlayerStatus(
  player: PlayerStatusSource,
  status: VideoPlayerStatus = player.status,
): PlayerStatusSnapshot {
  return { player, status };
}

export function playerStatusFor(
  snapshot: PlayerStatusSnapshot,
  player: PlayerStatusSource,
): VideoPlayerStatus {
  return snapshot.player === player ? snapshot.status : player.status;
}

export function shouldPlayVideo(
  active: boolean,
  hasPlayableSource: boolean,
  status: VideoPlayerStatus,
): boolean {
  return active && hasPlayableSource && status === 'readyToPlay';
}

export function videoPresentation(
  hasSource: boolean,
  probeState: VideoProbeState,
  hasPlayableSource: boolean,
  playerStatus: VideoPlayerStatus,
): VideoPresentation {
  if (!hasSource || probeState === 'unavailable') return 'unavailable';
  if (probeState === 'error') return 'error';
  if (probeState === 'idle') return 'loading';
  if (probeState === 'probing') return 'probing';
  if (probeState === 'preparing') return 'preparing';
  if (!hasPlayableSource) return 'loading';
  if (playerStatus === 'error') return 'error';
  return playerStatus === 'readyToPlay' ? 'ready' : 'loading';
}

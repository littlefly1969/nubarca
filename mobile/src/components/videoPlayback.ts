import type { VideoPlayerStatus } from 'expo-video';

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

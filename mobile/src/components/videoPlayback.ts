import type { VideoPlayerStatus } from 'expo-video';

export type VideoProbeState = 'probing' | 'ready' | 'preparing' | 'unavailable';
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
  if (probeState === 'probing') return 'probing';
  if (probeState === 'preparing') return 'preparing';
  if (!hasPlayableSource) return 'loading';
  if (playerStatus === 'error') return 'error';
  return playerStatus === 'readyToPlay' ? 'ready' : 'loading';
}

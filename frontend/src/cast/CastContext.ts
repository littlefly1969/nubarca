import { createContext } from 'react';
import type { CastPlaybackMode } from '@nubarca/api-client';

// Why Cast state is global rather than owned by MediaViewer.
//
// A cast outlives the thing that started it. The user opens a video, sends it to
// the television, and then closes the viewer to go and look at something else —
// and the television must keep playing. If the session lived inside the viewer,
// closing it would unmount the RemotePlayer, drop the listeners and (worse)
// leave a live grant nobody can revoke. So the provider is mounted in the
// authenticated shell, above every page and every viewer, and the viewer merely
// asks it to do things.

/** Why casting is or is not offered. Only `available` shows the launcher. */
export type CastAvailability =
  /** Still resolving (SDK loading). */
  | 'unknown'
  /** No Chrome Cast bridge: Firefox, Safari, and every browser on iOS/iPadOS. */
  | 'unsupported'
  /** The Web Sender refuses to run outside a secure context. */
  | 'insecure-origin'
  /** Secure, but on a hostname no receiver on the network can resolve. */
  | 'unreachable-origin'
  /** The account does not hold `cast.access`. */
  | 'no-permission'
  /** The SDK loaded but reported a failure. */
  | 'failed'
  | 'available';

export type CastSessionState = 'none' | 'connecting' | 'connected';

/** What the sender is doing about the CURRENT item, independent of the session. */
export type CastPlaybackStatus = 'idle' | 'preparing' | 'loading' | 'playing' | 'error';

export type CastError =
  /** The grant could not be minted (permission, ownership, server error). */
  | 'grant'
  /** The HLS ladder did not become ready within the bounded wait. */
  | 'preparing'
  /** The receiver refused the media — usually a codec it cannot decode. */
  | 'media'
  | null;

/** Live mirror of the receiver, fed by RemotePlayer events. */
export interface CastRemoteState {
  deviceName: string | null;
  title: string | null;
  fileId: string | null;
  mode: CastPlaybackMode | null;
  isPaused: boolean;
  isMuted: boolean;
  currentTime: number;
  duration: number;
  volumeLevel: number;
}

/** What the viewer hands over when the user presses Cast. */
export interface CastRequest {
  fileId: string;
  title: string;
  subtitle?: string | null;
  /** Where local playback had reached, so the television resumes there. */
  positionSeconds: number;
}

/**
 * Where local playback should resume after a cast ends. Written when a session
 * finishes for any reason — explicit stop, receiver disconnect, network loss —
 * and read exactly once by whoever is showing that file.
 */
export interface CastHandoff {
  fileId: string;
  positionSeconds: number;
}

export interface CastContextValue {
  availability: CastAvailability;
  sessionState: CastSessionState;
  status: CastPlaybackStatus;
  error: CastError;
  remote: CastRemoteState | null;

  /** Send (or replace) the item playing on the receiver. */
  castVideo: (request: CastRequest) => Promise<void>;
  playOrPause: () => void;
  seek: (seconds: number) => void;
  setVolume: (level: number) => void;
  toggleMute: () => void;
  /** Stop the receiver, revoke the grant and end the session. */
  stopCasting: () => Promise<void>;

  /** Take the pending resume position, clearing it. Null when there is none. */
  consumeHandoff: (fileId: string) => number | null;
}

export const CastContext = createContext<CastContextValue | null>(null);

// Background/foreground policy for the ONE video player. Pure, node-testable.
//
// WHAT CHANGED AND WHY
// --------------------
// The player used to PAUSE on background and keep the native ExoPlayer alive.
// On a constrained television that is a decoder, a MediaSession and an audio-
// focus registration retained for as long as the user is somewhere else. The
// product does not do background playback, so nothing justifies holding them.
//
// The mechanism is deliberately not a new native call: expo-video's
// `useVideoPlayer` releases its native player when the owning component
// unmounts, so the policy below simply decides whether that component is
// rendered. Releasing through the documented lifecycle means the MediaSession
// and the audio-focus registration go with it, without NubArca ever owning
// either.
//
// TRANSIENT BLUR IS NOT BACKGROUND
// --------------------------------
// React Native reports 'inactive' for brief interruptions that are not an
// Activity stop. Treating those as background would tear down and re-prepare
// ExoPlayer on incidental focus changes — expensive, visible as a black frame,
// and a decoder churn this policy is meant to avoid. Only a real 'background'
// transition releases.

export type HostState = 'active' | 'inactive' | 'background';

export interface PlaybackSnapshot {
  /** The source the position belongs to. A snapshot is worthless without it. */
  readonly source: string;
  readonly positionSeconds: number;
  /** Whether playback was running when we were interrupted. */
  readonly wasPlaying: boolean;
}

/**
 * Should the player component be MOUNTED (and therefore its native player
 * alive) for this host state?
 */
export function shouldMountPlayer(host: HostState): boolean {
  return host !== 'background';
}

/**
 * Is this transition a genuine background stop that must release the player?
 *
 * 'inactive' is excluded on purpose — see the note above.
 */
export function releasesPlayer(previous: HostState, next: HostState): boolean {
  return next === 'background' && previous !== 'background';
}

/** Is this transition a genuine return from background? */
export function resumesFromBackground(previous: HostState, next: HostState): boolean {
  return previous === 'background' && next === 'active';
}

/**
 * The position to restore on the recreated player, or null.
 *
 * Null when there is no snapshot, or when it belongs to a DIFFERENT source —
 * seeking one video to another video's timestamp is a worse outcome than
 * starting from the beginning, and it is the failure a naive "restore the last
 * position" would produce after the user changes item while backgrounded.
 */
export function restorablePosition(
  snapshot: PlaybackSnapshot | null,
  currentSource: string,
): number | null {
  if (snapshot === null) return null;
  if (snapshot.source !== currentSource) return null;
  if (!Number.isFinite(snapshot.positionSeconds) || snapshot.positionSeconds < 0) return null;
  return snapshot.positionSeconds;
}

/**
 * Should playback START by itself after the player is recreated?
 *
 * Always NO. Returning to a room and having audio begin on its own is the
 * behaviour this product deliberately avoids, and it stays deliberate even when
 * the user WAS playing before the interruption — `wasPlaying` is recorded so the
 * UI can reflect intent, never to auto-resume. The user presses SELECT.
 */
export function shouldAutoResume(): boolean {
  return false;
}

// --- output routes -----------------------------------------------------------

/**
 * What losing the audio output route means for playback.
 *
 * HDMI unplugged, an AV receiver switched away, a Bluetooth speaker
 * disconnecting: pause and keep the position. Never navigate, never close the
 * viewer, never reset position — the user has not asked to stop watching, only
 * the sound has nowhere to go.
 */
export function pausesOnOutputLoss(hasPlaybackContext: boolean): boolean {
  return hasPlaybackContext;
}

/**
 * And when the route comes back: still NO auto-resume, for the same reason as
 * returning from background. Predictable beats clever on a device in a living
 * room.
 */
export function resumesOnOutputRestored(): boolean {
  return false;
}

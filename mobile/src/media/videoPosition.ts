// Surviving a remount of the video slide.
//
// THE DEFECT THIS ANSWERS, reported from a physical device: rotating the phone
// restarted the video from zero. The viewer's pager re-measures on a rotation
// and remounts its cells, and a remounted expo-video player is a NEW player —
// it has no idea where the old one was.
//
// The position therefore has to be kept somewhere that outlives the slide. It
// is deliberately NOT React state inside the slide (that is the thing being
// destroyed) and NOT a context threaded through the pager (the pager is not
// interested). It is a small module-owned store, with the same rule the TV
// client already uses: a position is restorable only for the SAME source.
//
// Seeking one video to another video's timestamp is a worse outcome than
// starting from the beginning, and it is exactly what a naive "restore the
// last position" produces once the user changes item.
//
// PRIVACY: positions are per-session and in memory only. `forgetAllPositions`
// is called when the viewer closes and on any identity change, so nothing a
// previous account watched can survive into the next one.

export interface PlaybackPosition {
  /** The exact source URI the position belongs to. */
  source: string;
  positionSeconds: number;
}

const positions = new Map<string, number>();

/** Remember where a slide was, just before it goes away. */
export function rememberPosition(source: string, positionSeconds: number): void {
  if (source.length === 0) return;
  if (!Number.isFinite(positionSeconds) || positionSeconds < 0) return;
  positions.set(source, positionSeconds);
}

export function recallPosition(source: string): PlaybackPosition | null {
  const seconds = positions.get(source);
  if (seconds === undefined) return null;
  return { source, positionSeconds: seconds };
}

export function forgetPosition(source: string): void {
  positions.delete(source);
}

/** Called when the viewer closes and whenever the signed-in identity changes. */
export function forgetAllPositions(): void {
  positions.clear();
}

/**
 * The position a freshly mounted player may seek to, or null to start at the
 * beginning.
 *
 * Refuses a snapshot belonging to a different source, a nonsensical value, and
 * — the case a bare "same source" check misses — a position at or past the
 * end. Restoring there would show the last frame and immediately report the
 * video as finished, which for a slideshow means advancing instantly.
 */
export function restorablePosition(
  snapshot: PlaybackPosition | null,
  currentSource: string,
  durationSeconds: number | null = null,
): number | null {
  if (snapshot === null) return null;
  if (snapshot.source !== currentSource) return null;
  if (!Number.isFinite(snapshot.positionSeconds) || snapshot.positionSeconds < 0) return null;
  if (
    durationSeconds !== null
    && Number.isFinite(durationSeconds)
    && durationSeconds > 0
    && snapshot.positionSeconds >= durationSeconds - 0.25
  ) {
    return null;
  }
  // A position within the first moment is not worth a seek: it is where a new
  // player already is, and seeking would cost a buffer round trip for nothing.
  return snapshot.positionSeconds < 0.5 ? null : snapshot.positionSeconds;
}

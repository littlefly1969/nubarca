// THE ONE keep-awake decision for NubArca TV. Pure, node-testable.
//
// WHAT WAS WRONG
// --------------
// Both viewers called `useScreenAwake(true)` for their entire mounted
// lifetime. That is not "keep the television awake while media plays" — it is
// "keep the television awake because a picture is on screen". A user who opened
// one photograph and walked away kept a TV panel lit indefinitely, and the
// platform's own ambient/screensaver behaviour — which exists precisely to stop
// that — was suppressed by an app that had nothing to show.
//
// WHO OWNS WHAT, ONCE
// -------------------
// video   → expo-video's `keepScreenOnWhilePlaying`. It already tracks ACTUAL
//           playback, so it releases on pause, on error and on buffering-without-
//           playback for free. NubArca adds NOTHING here: stacking expo-keep-awake
//           on top would be a second authority that can disagree with the first,
//           and the one that gets stuck holding a lock is always the redundant one.
// photo   → NubArca, and ONLY while the slideshow is actually rotating. A still
//           photograph the user is looking at is not an animation and does not
//           justify defeating the screensaver.
//
// Background is part of the decision rather than a separate cleanup path,
// because "the slideshow is playing" stops being true the moment the app is
// behind HOME — the timers must stop and the lock must go with them.

export type ViewerMediaKind = 'photo' | 'video';

export interface WakeInputs {
  /** What the viewer is currently showing. */
  readonly kind: ViewerMediaKind;
  /** True when the photo slideshow is rotating (not merely available). */
  readonly slideshowPlaying: boolean;
  /** False once the app is genuinely backgrounded. */
  readonly hostActive: boolean;
}

/**
 * Should NubArca hold an explicit keep-awake lock?
 *
 * True in exactly one case: a photo slideshow actually rotating in the
 * foreground. Everything else — a still photo, a paused slideshow, any video
 * state at all, anything in the background — is false, either because there is
 * nothing to keep awake for or because someone else already owns it.
 */
export function shouldKeepPhotoSlideshowAwake(inputs: WakeInputs): boolean {
  if (!inputs.hostActive) return false;
  // Video is expo-video's to own. Returning true here would be the second
  // authority this module exists to prevent.
  if (inputs.kind !== 'photo') return false;
  return inputs.slideshowPlaying;
}

/**
 * Whether the photo slideshow timer should be running at all.
 *
 * Identical inputs to the wake lock and deliberately the same answer: a
 * slideshow that keeps advancing photographs behind HOME is doing work nobody
 * can see, and it would drag the wake lock along with it. Keeping the two
 * decisions in one module is what stops them drifting apart.
 */
export function shouldRotateSlideshow(inputs: WakeInputs): boolean {
  return shouldKeepPhotoSlideshowAwake(inputs);
}

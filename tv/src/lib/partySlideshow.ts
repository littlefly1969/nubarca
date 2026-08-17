// Party slideshow timing policy — pure, node-testable, no React, no player.
//
// ONE ordered sequence carries photos and videos together, so the only thing
// that differs between them is WHEN the sequence advances:
//   photo → after a fixed dwell time;
//   video → at its natural end, or at a cap, whichever comes first.
//
// Two rules make the video case harder than it looks, and both live here rather
// than in the component so they can be tested without a device:
//
//  1. THE CAP IS MEDIA TIME, NOT WALL CLOCK. A setTimeout would keep running
//     while the video is paused or rebuffering, so a guest who paused a 60 s cap
//     for two minutes would return to find the slideshow had already moved on
//     — the cap is meant to bound how long a video HOLDS the screen playing,
//     not how long the viewer is allowed to look at it.
//  2. IT ADVANCES EXACTLY ONCE. A video that reaches its cap on the same frame
//     it ends would otherwise fire both the cap rule and the player's
//     playToEnd, skipping the next item entirely. The latch below is the single
//     place that can say "advance", and it says it once per video.

export interface PartySlideshowTiming {
  photoSeconds: number;
  maxVideoSeconds: number;
}

// The historical hardcoded interval. Still the answer for every NON-party
// slideshow, which has no server-configured timing at all.
export const DEFAULT_PHOTO_SLIDE_MS = 9000;

// How long a video may sit in probing/preparing while the slideshow is playing
// before it is skipped. A party wall must not stop because one clip is still
// being transcoded; the item is retried normally on the next time round.
export const VIDEO_PREPARING_GRACE_MS = 10_000;

// Defensive bounds mirroring the server's validated ranges. The server is the
// validator, but a stale or hostile response must not be able to set a 0 ms
// photo interval (a strobing wall) or an unbounded video.
const MIN_PHOTO_SECONDS = 3;
const MAX_PHOTO_SECONDS = 60;
const MIN_VIDEO_SECONDS = 5;
const MAX_VIDEO_SECONDS = 600;

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.min(max, Math.max(min, Math.round(value)));
}

// How long the CURRENT photo should hold the screen. Non-party keeps the
// historical constant, so nothing about an ordinary album slideshow changes.
export function photoSlideMs(timing: PartySlideshowTiming | null): number {
  if (timing === null) return DEFAULT_PHOTO_SLIDE_MS;
  return clamp(timing.photoSeconds, MIN_PHOTO_SECONDS, MAX_PHOTO_SECONDS) * 1000;
}

// The cap for the CURRENT video, in seconds of playback, or null when the video
// should simply play to its end (every non-party slideshow).
export function videoCapSeconds(timing: PartySlideshowTiming | null): number | null {
  if (timing === null) return null;
  return clamp(timing.maxVideoSeconds, MIN_VIDEO_SECONDS, MAX_VIDEO_SECONDS);
}

// ------------------------------------------------------------------ rotation

// The per-video latch. Recreated for each video (the player is keyed by source,
// so a new video is a new component instance and a new rotation).
export interface VideoRotation {
  readonly capSeconds: number | null;
  readonly advanced: boolean;
}

export function beginVideoRotation(capSeconds: number | null): VideoRotation {
  return { capSeconds, advanced: false };
}

export interface RotationStep {
  readonly state: VideoRotation;
  readonly advance: boolean;
}

function latch(state: VideoRotation): RotationStep {
  // Second and later callers get advance:false, which is what makes "cap and
  // playToEnd on the same frame" a single advance instead of a skipped item.
  if (state.advanced) return { state, advance: false };
  return { state: { ...state, advanced: true }, advance: true };
}

// The player reported a playback position. `mediaTimeSeconds` is the video's
// OWN clock, so time spent paused or buffering simply does not arrive here and
// therefore cannot consume the cap. A seek past the cap lands beyond it and
// advances on the next report, which is the same rule with no special case.
export function onVideoProgress(state: VideoRotation, mediaTimeSeconds: number): RotationStep {
  if (state.capSeconds === null) return { state, advance: false };
  if (!Number.isFinite(mediaTimeSeconds) || mediaTimeSeconds < state.capSeconds) {
    return { state, advance: false };
  }
  return latch(state);
}

// The video reached its natural end before the cap.
export function onVideoEnded(state: VideoRotation): RotationStep {
  return latch(state);
}

// ------------------------------------------------------------------ preparing

// Whether the slideshow should start the preparing grace window. Only an
// AUTOPLAYING party slideshow skips a video it cannot play: a paused or
// manually-driven viewer must stay exactly where the user put it, because there
// the user is the one deciding when to move on.
export function shouldArmPreparingGrace(input: {
  partyEnabled: boolean;
  playing: boolean;
  isVideo: boolean;
  videoReady: boolean;
}): boolean {
  return input.partyEnabled && input.playing && input.isVideo && !input.videoReady;
}

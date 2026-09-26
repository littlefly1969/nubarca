// Party slideshow timing — pure, testable without a DOM or a player.
//
// A PORT of tv/src/lib/partySlideshow.ts, held to it by `nativeParity.test.ts`.
// ONE ordered sequence carries photos and videos together; the only difference
// between them is WHEN the sequence advances:
//   photo → after the party's dwell time;
//   video → at its natural end, or at the party's cap, whichever comes first.
//
// The cap is MEDIA time, not wall clock (a paused or rebuffering video does not
// spend it), and a video advances EXACTLY ONCE even when the cap and its end
// land on the same frame.

export interface PartySlideshowTiming {
  photoSeconds: number;
  maxVideoSeconds: number;
}

/** The historical interval, still the answer for every NON-party slideshow. */
export const DEFAULT_PHOTO_SLIDE_MS = 9000;

/** How long a video may stay unplayable on a rotating party wall before it is skipped. */
export const VIDEO_PREPARING_GRACE_MS = 10_000;

const MIN_PHOTO_SECONDS = 3;
const MAX_PHOTO_SECONDS = 60;
const MIN_VIDEO_SECONDS = 5;
const MAX_VIDEO_SECONDS = 600;

function clamp(value: number, min: number, max: number): number {
  if (!Number.isFinite(value)) return min;
  return Math.min(max, Math.max(min, Math.round(value)));
}

/** How long the CURRENT photo holds the screen. */
export function photoSlideMs(timing: PartySlideshowTiming | null): number {
  if (timing === null) return DEFAULT_PHOTO_SLIDE_MS;
  return clamp(timing.photoSeconds, MIN_PHOTO_SECONDS, MAX_PHOTO_SECONDS) * 1000;
}

/** The CURRENT video's cap in seconds of playback, or null to play to its end. */
export function videoCapSeconds(timing: PartySlideshowTiming | null): number | null {
  if (timing === null) return null;
  return clamp(timing.maxVideoSeconds, MIN_VIDEO_SECONDS, MAX_VIDEO_SECONDS);
}

/** The per-video latch: a new video is a new rotation. */
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
  if (state.advanced) return { state, advance: false };
  return { state: { ...state, advanced: true }, advance: true };
}

/** The player reported its OWN position; paused time never arrives here. */
export function onVideoProgress(state: VideoRotation, mediaTimeSeconds: number): RotationStep {
  if (state.capSeconds === null) return { state, advance: false };
  if (!Number.isFinite(mediaTimeSeconds) || mediaTimeSeconds < state.capSeconds) {
    return { state, advance: false };
  }
  return latch(state);
}

/** The video reached its natural end before the cap. */
export function onVideoEnded(state: VideoRotation): RotationStep {
  return latch(state);
}

/** Only a ROTATING party wall skips a video it cannot play. */
export function shouldArmPreparingGrace(input: {
  slideshowMode: boolean;
  partyEnabled: boolean;
  playing: boolean;
  isVideo: boolean;
  videoReady: boolean;
}): boolean {
  return input.slideshowMode && input.partyEnabled && input.playing
    && input.isVideo && !input.videoReady;
}

export type PlayPauseAction =
  | 'toggle-slideshow'
  | 'toggle-video-player'
  | 'promote-to-slideshow';

/** In a slideshow there is ONE play state, governing photos and videos alike. */
export function resolvePlayPause(input: {
  slideshowMode: boolean;
  isVideo: boolean;
}): PlayPauseAction {
  if (input.slideshowMode) return 'toggle-slideshow';
  return input.isVideo ? 'toggle-video-player' : 'promote-to-slideshow';
}

export interface VideoPlaybackProps {
  readonly maxPlaybackSeconds: number | null;
  readonly playing: boolean | undefined;
}

/** What the video player is handed; `playing: undefined` means "not controlled". */
export function videoPlaybackProps(input: {
  slideshowMode: boolean;
  partyEnabled: boolean;
  playing: boolean;
  timing: PartySlideshowTiming | null;
}): VideoPlaybackProps {
  return {
    maxPlaybackSeconds: input.slideshowMode && input.partyEnabled
      ? videoCapSeconds(input.timing)
      : null,
    playing: input.slideshowMode ? input.playing : undefined,
  };
}

export function photoRotationActive(input: {
  slideshowMode: boolean;
  playing: boolean;
}): boolean {
  return input.slideshowMode && input.playing;
}

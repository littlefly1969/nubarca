// Album Play, as a state machine with no React in it.
//
// Play is a VIEWER operation: it opens items one after another and mutates
// nothing, which is exactly why a shared-album Viewer is allowed to run it. It
// is not Party and it is not Show-on-TV — those are the owner's publication
// settings, and they are not reachable from here.

export type MediaPlayKind = 'image' | 'video';

// How long a photo stays on screen before the sequence moves on. Long enough to
// look at, short enough that an album of hundreds is not an evening.
export const PLAY_PHOTO_DURATION_MS = 5000;

export type PlayStep =
  // Show this index next.
  | { kind: 'advance'; index: number }
  // The sequence has run out of LOADED items but the server has more: hold
  // here and let the next page arrive. Play must not end early merely because
  // pagination has not caught up.
  | { kind: 'wait' }
  // The real end of the sequence.
  | { kind: 'finish' };

export interface PlayStepInput {
  // The item currently on screen.
  index: number;
  // How many items are loaded in the CURRENT sequence — which is the filtered
  // one. Play never reaches past the filter into the rest of the album.
  count: number;
  // Whether the server has further pages of this same filtered sequence.
  hasMore: boolean;
}

export function nextPlayStep({ index, count, hasMore }: PlayStepInput): PlayStep {
  const next = index + 1;
  if (next < count) return { kind: 'advance', index: next };
  return hasMore ? { kind: 'wait' } : { kind: 'finish' };
}

// How long the current item should hold the screen before the sequence advances
// on its own. A video advances when it ENDS, not on a timer — a clock would cut
// off anything longer than the interval and linger on anything shorter — so it
// has no duration here at all.
export function playHoldMilliseconds(
  kind: MediaPlayKind | undefined,
  photoDurationMs: number = PLAY_PHOTO_DURATION_MS,
): number | null {
  return kind === 'image' ? photoDurationMs : null;
}

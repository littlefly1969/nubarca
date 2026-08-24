import { useCallback, useEffect, useRef, useState } from 'react';
import {
  PLAY_PHOTO_DURATION_MS,
  nextPlayStep,
  playHoldMilliseconds,
  type MediaPlayKind,
} from './sequencePlayback';

export interface SequencePlaybackInput {
  // The CURRENT sequence: whatever is on screen after the active filter. Play
  // plays what the user is looking at, never the hidden rest of the album.
  count: number;
  // The item the viewer has open, or null when it is closed.
  index: number | null;
  kindAt(index: number): MediaPlayKind | undefined;
  // Open the viewer at an index (used by `start`) and move it (used to
  // advance). Two callbacks because opening and navigating are different
  // operations on every surface this drives.
  onOpen(index: number): void;
  onIndexChange(index: number): void;
  // Further pages of the same sequence exist; ask for them rather than ending.
  hasMore?: boolean;
  onNeedMore?(): void;
  photoDurationMs?: number;
}

export interface SequencePlayback {
  active: boolean;
  // The sequence reached its last item. The viewer stays open on it, so
  // "replay" is an offer rather than something that has already happened.
  finished: boolean;
  start(index?: number): void;
  stop(): void;
  replay(): void;
  // Hand to the viewer: a video advances when it ends, never on a clock.
  onVideoEnded(): void;
}

/**
 * Drives a viewer through a sequence: photos for a bounded moment, videos until
 * they end, stopping at the last item.
 *
 * The caller keeps ownership of the index — this hook only decides WHEN to move
 * it. That is what lets one hook run the owner's album workspace and a
 * recipient's shared album without either of them handing over its state, and
 * why "which items" is never a question it can answer differently from the wall
 * the user is looking at.
 */
export function useSequencePlayback({
  count, index, kindAt, onOpen, onIndexChange,
  hasMore = false, onNeedMore, photoDurationMs = PLAY_PHOTO_DURATION_MS,
}: SequencePlaybackInput): SequencePlayback {
  const [active, setActive] = useState(false);
  const [finished, setFinished] = useState(false);
  // Parked at the end of the loaded items with more still to come. Not the same
  // as finished: nothing has ended, the next page simply has not arrived.
  const [waiting, setWaiting] = useState(false);

  // Latest inputs, so the advancing effect is not torn down (and a photo's
  // timer not restarted) merely because a parent re-rendered.
  const latest = useRef({ count, index, hasMore, onIndexChange, onNeedMore });
  latest.current = { count, index, hasMore, onIndexChange, onNeedMore };

  const advance = useCallback(() => {
    const current = latest.current;
    if (current.index === null) return;
    const step = nextPlayStep({
      index: current.index, count: current.count, hasMore: current.hasMore,
    });
    if (step.kind === 'advance') {
      setWaiting(false);
      current.onIndexChange(step.index);
      return;
    }
    if (step.kind === 'wait') {
      setWaiting(true);
      current.onNeedMore?.();
      return;
    }

    // The end: playback stops, the viewer stays where it is. Closing it here
    // would snatch the last photo away the instant the album finished.
    setWaiting(false);
    setActive(false);
    setFinished(true);
  }, []);

  const start = useCallback((from = 0) => {
    if (latest.current.count === 0) return;
    setFinished(false);
    setWaiting(false);
    setActive(true);
    onOpen(from);
  }, [onOpen]);

  const stop = useCallback(() => {
    setActive(false);
    setFinished(false);
    setWaiting(false);
  }, []);

  const replay = useCallback(() => { start(0); }, [start]);

  // The kind of the item on screen, resolved during render so the timer effect
  // below depends on a VALUE rather than on the identity of a callback.
  const currentKind = index === null ? undefined : kindAt(index);

  // A photo holds the screen for a bounded moment. Re-armed per item and torn
  // down whenever playback stops or the viewer closes, so a timer from a stopped
  // run can never move a later item.
  useEffect(() => {
    if (!active || index === null || waiting) return;
    const hold = playHoldMilliseconds(currentKind, photoDurationMs);
    if (hold === null) return;
    const timer = setTimeout(advance, hold);
    return () => clearTimeout(timer);
  }, [active, index, currentKind, waiting, photoDurationMs, advance]);

  // The viewer closed under an active run (Escape, the backdrop, a navigation):
  // playback ends with it rather than continuing invisibly.
  useEffect(() => {
    if (active && index === null) {
      setActive(false);
      setWaiting(false);
    }
  }, [active, index]);

  // Parked at the end of a page: resume as soon as the next one lands, and end
  // properly if it turns out there was nothing more after all.
  useEffect(() => {
    if (!active || !waiting || index === null) return;
    if (index + 1 < count) {
      setWaiting(false);
      latest.current.onIndexChange(index + 1);
      return;
    }
    if (!hasMore) {
      setWaiting(false);
      setActive(false);
      setFinished(true);
    }
  }, [active, waiting, index, count, hasMore]);

  const onVideoEnded = useCallback(() => {
    if (!active) return;
    advance();
  }, [active, advance]);

  return { active, finished, start, stop, replay, onVideoEnded };
}

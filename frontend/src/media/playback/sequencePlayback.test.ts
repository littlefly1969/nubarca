import { describe, expect, it } from 'vitest';
import { PLAY_PHOTO_DURATION_MS, nextPlayStep, playHoldMilliseconds } from './sequencePlayback';

describe('album play steps', () => {
  it('advances through the loaded sequence', () => {
    expect(nextPlayStep({ index: 0, count: 3, hasMore: false }))
      .toEqual({ kind: 'advance', index: 1 });
  });

  it('waits for the next page rather than ending early', () => {
    // Play must not stop merely because pagination has not caught up: the
    // sequence is the album, not the page.
    expect(nextPlayStep({ index: 2, count: 3, hasMore: true })).toEqual({ kind: 'wait' });
  });

  it('finishes at the real end', () => {
    expect(nextPlayStep({ index: 2, count: 3, hasMore: false })).toEqual({ kind: 'finish' });
  });

  it('finishes a single-item sequence immediately after it', () => {
    expect(nextPlayStep({ index: 0, count: 1, hasMore: false })).toEqual({ kind: 'finish' });
  });
});

describe('album play hold', () => {
  it('holds a photo for a bounded moment', () => {
    expect(playHoldMilliseconds('image')).toBe(PLAY_PHOTO_DURATION_MS);
    expect(playHoldMilliseconds('image', 1200)).toBe(1200);
  });

  it('never puts a video on a clock', () => {
    // A timer would cut off anything longer than the interval and linger on
    // anything shorter — a video advances when it ENDS.
    expect(playHoldMilliseconds('video')).toBeNull();
    expect(playHoldMilliseconds(undefined)).toBeNull();
  });
});

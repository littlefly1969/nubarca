import { expect, it } from 'vitest';
import { formatTrackInterval, formatTrackPosition } from './videoTrackTime';

// VFACE-02: a video interval is a position on the clip's own timeline, so it must
// render as a duration offset (m:ss / h:mm:ss), never through date formatting.

it('formats positions as m:ss below one hour', () => {
  expect(formatTrackPosition(0)).toBe('0:00');
  expect(formatTrackPosition(5_000)).toBe('0:05');
  expect(formatTrackPosition(65_000)).toBe('1:05');
  expect(formatTrackPosition(599_000)).toBe('9:59');
});

it('adds hours only once past one hour', () => {
  expect(formatTrackPosition(3_599_000)).toBe('59:59');
  expect(formatTrackPosition(3_600_000)).toBe('1:00:00');
  expect(formatTrackPosition(3_725_000)).toBe('1:02:05');
});

it('truncates sub-second remainders instead of rounding up', () => {
  // 5.9 s is still inside the 5th second: a label must never point past the
  // frame the evidence actually came from.
  expect(formatTrackPosition(5_900)).toBe('0:05');
});

it('formats an interval as a range', () => {
  expect(formatTrackInterval(65_000, 92_000)).toBe('1:05 – 1:32');
});

it('collapses a single-instant interval to one label', () => {
  expect(formatTrackInterval(65_000, 65_400)).toBe('1:05');
});

it('is defensive about impossible input', () => {
  expect(formatTrackPosition(-1)).toBe('0:00');
  expect(formatTrackPosition(Number.NaN)).toBe('0:00');
  expect(formatTrackPosition(Number.POSITIVE_INFINITY)).toBe('0:00');
});

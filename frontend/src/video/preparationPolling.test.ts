import { describe, expect, it } from 'vitest';
import {
  MAX_RETRY_AFTER_MS,
  PREPARATION_BACKOFF_MS,
  backoffStepMs,
  nextPreparationDelayMs,
  parseRetryAfterMs,
} from './preparationPolling';

describe('parseRetryAfterMs', () => {
  it('reads delta-seconds', () => {
    expect(parseRetryAfterMs('2')).toBe(2000);
    expect(parseRetryAfterMs(' 10 ')).toBe(10_000);
    expect(parseRetryAfterMs('0')).toBe(0);
  });

  it('ignores an absent, empty or unparseable header', () => {
    for (const header of [null, undefined, '', '   ', 'soon', 'Wed, 21 Oct 2026 07:28:00 GMT']) {
      expect(parseRetryAfterMs(header), String(header)).toBeNull();
    }
  });

  it('ignores a negative delay', () => {
    expect(parseRetryAfterMs('-5')).toBeNull();
  });

  it('clamps an absurd delay so a bad header cannot stall the player', () => {
    expect(parseRetryAfterMs('86400')).toBe(MAX_RETRY_AFTER_MS);
  });
});

describe('nextPreparationDelayMs', () => {
  it('ramps 1.5s -> 2.5s -> 5s without a server hint', () => {
    expect(PREPARATION_BACKOFF_MS).toEqual([1500, 2500, 5000]);
    expect(nextPreparationDelayMs(0)).toBe(1500);
    expect(nextPreparationDelayMs(1)).toBe(2500);
    expect(nextPreparationDelayMs(2)).toBe(5000);
  });

  it('caps at the last step instead of growing without bound', () => {
    expect(nextPreparationDelayMs(3)).toBe(5000);
    expect(nextPreparationDelayMs(50)).toBe(5000);
    expect(backoffStepMs(-1)).toBe(1500);
  });

  it('honours a Retry-After that asks us to wait LONGER', () => {
    expect(nextPreparationDelayMs(0, '12')).toBe(12_000);
    expect(nextPreparationDelayMs(2, '20')).toBe(20_000);
  });

  it('treats Retry-After as a floor, so it cannot defeat the backoff', () => {
    // The endpoint sends a small constant (2 s). Obeying it literally would pin
    // polling there forever; RFC 9110 says it is a MINIMUM wait, so the local
    // ramp still governs once it exceeds the hint.
    expect(nextPreparationDelayMs(0, '2')).toBe(2000); // hint > ramp step 1500
    expect(nextPreparationDelayMs(1, '2')).toBe(2500); // ramp overtakes it
    expect(nextPreparationDelayMs(9, '2')).toBe(5000); // and keeps the cap
  });

  it('falls back to the ramp for a junk header', () => {
    expect(nextPreparationDelayMs(1, 'later')).toBe(2500);
    expect(nextPreparationDelayMs(1, null)).toBe(2500);
  });
});

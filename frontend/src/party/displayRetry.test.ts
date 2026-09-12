import { describe, expect, it } from 'vitest';
import {
  DISPLAY_RETRY_BASE_MS,
  DISPLAY_RETRY_MAX_MS,
  displayRetryDelayMs,
  isRetryableDisplayFailure,
} from './displayRetry';

describe('the paired display retry schedule', () => {
  it('backs off exponentially and is capped', () => {
    expect(displayRetryDelayMs(0)).toBe(DISPLAY_RETRY_BASE_MS);
    expect(displayRetryDelayMs(1)).toBe(DISPLAY_RETRY_BASE_MS * 2);
    expect(displayRetryDelayMs(4)).toBe(DISPLAY_RETRY_BASE_MS * 16);
    expect(displayRetryDelayMs(40)).toBe(DISPLAY_RETRY_MAX_MS);
    expect(displayRetryDelayMs(-1)).toBe(DISPLAY_RETRY_BASE_MS);
    for (let attempt = 0; attempt < 60; attempt += 1) {
      expect(displayRetryDelayMs(attempt)).toBeLessThanOrEqual(DISPLAY_RETRY_MAX_MS);
    }
  });

  it('retries only what is not an answer', () => {
    for (const status of [null, 408, 429, 500, 502, 503, 504]) {
      expect(isRetryableDisplayFailure(status)).toBe(true);
    }
    // A 401 is the shell's to act on (a new grant); any other 4xx will not
    // change by asking again.
    for (const status of [400, 401, 403, 404, 410]) {
      expect(isRetryableDisplayFailure(status)).toBe(false);
    }
  });
});

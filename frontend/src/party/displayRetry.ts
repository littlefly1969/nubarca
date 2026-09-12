// How the paired-display page retries what it fetches besides the snapshot.
//
// The snapshot already recovers by polling. The lobby's QR and an activity's
// photograph are fetched ONCE each, and a single network blip used to leave the
// lobby without its code, or the card without its picture, for as long as the
// scene lasted. They now retry — on a capped backoff, only for failures that
// are not an answer, and only for as long as the thing they are for is still
// on screen. Pure, so the schedule is a test rather than a claim.

export const DISPLAY_RETRY_BASE_MS = 1_000;
export const DISPLAY_RETRY_MAX_MS = 30_000;

/** The wait before retry number `attempt` (0-based). Capped: never a tight loop. */
export function displayRetryDelayMs(attempt: number): number {
  const steps = Math.max(0, Math.min(attempt, 10));
  return Math.min(DISPLAY_RETRY_MAX_MS, DISPLAY_RETRY_BASE_MS * 2 ** steps);
}

/**
 * Whether a failure is worth asking again. `status` is the HTTP status, or null
 * when no response arrived. No response, a timeout, a throttle and a server
 * error may all clear up; any other 4xx is the server's considered answer — and
 * a 401 in particular is not retried here at all, because it is the shell's to
 * act on (it mints a new grant).
 */
export function isRetryableDisplayFailure(status: number | null): boolean {
  return status === null || status === 408 || status === 429 || status >= 500;
}

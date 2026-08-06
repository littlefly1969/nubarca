// How long to wait before re-probing a video whose HLS ladder is still being
// prepared (the endpoint's 202).
//
// The old behaviour was a flat 5 s. A short clip's ladder is often ready in
// well under that, so opening a just-uploaded video showed a poster and a
// status line for up to five seconds after playback was already possible.
//
// The replacement is a bounded ramp: check quickly at first, then back off so
// a genuinely long transcode is not hammered. The server's own estimate wins
// when it sends one.

/** Delays between successive 202 probes, in ms. Never exceeds the last entry. */
export const PREPARATION_BACKOFF_MS = [1500, 2500, 5000] as const;

/** Bound on a server-supplied Retry-After, so a bad header cannot stall us. */
export const MAX_RETRY_AFTER_MS = 30_000;

/**
 * Parse a `Retry-After` header.
 *
 * HTTP allows both delta-seconds and an HTTP-date; only the numeric form is
 * honoured here, because the date form needs a trusted clock and this endpoint
 * only ever produces the numeric one. Anything unparseable, negative or
 * non-finite returns null so the caller falls back to its own schedule.
 */
export function parseRetryAfterMs(header: string | null | undefined): number | null {
  if (header == null) return null;
  const raw = header.trim();
  // Number('') and Number('   ') are 0, not NaN — an empty header would
  // otherwise read as "retry immediately".
  if (raw === '') return null;
  const seconds = Number(raw);
  if (!Number.isFinite(seconds) || seconds < 0) return null;
  return Math.min(seconds * 1000, MAX_RETRY_AFTER_MS);
}

/** The local ramp step for probe number `attempt` (0-based), clamped at the end. */
export function backoffStepMs(attempt: number): number {
  const index = Math.min(Math.max(attempt, 0), PREPARATION_BACKOFF_MS.length - 1);
  return PREPARATION_BACKOFF_MS[index];
}

/**
 * The wait before probe number `attempt` (0-based) repeats.
 *
 * `Retry-After` is a MINIMUM wait, not an appointment (RFC 9110 §10.2.3): it
 * says "not before this", so it can only ever push the next probe later. That
 * distinction matters here — the endpoint is stateless and cannot estimate a
 * transcode, so it sends a small constant. Treating that constant as the exact
 * delay would pin polling at it forever and throw the backoff away; treating it
 * as a floor keeps the server's request honoured AND keeps a long transcode
 * backing off to the 5 s cap.
 */
export function nextPreparationDelayMs(
  attempt: number,
  retryAfterHeader?: string | null,
): number {
  return Math.max(backoffStepMs(attempt), parseRetryAfterMs(retryAfterHeader) ?? 0);
}

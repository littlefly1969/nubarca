// NubArca canonical /video delivery contract (VIDEO-DELIVERY-PARITY-01).
//
// SINGLE SOURCE OF TRUTH: shared/video-delivery/videoDelivery.ts.
// Byte-identical copies live at
//   frontend/src/video/videoDelivery.ts
//   mobile/src/media/videoDelivery.ts
//   tv/src/video/videoDelivery.ts
// Edit the shared original and run `scripts/sync-video-delivery.sh`; each
// project's videoDelivery test fails the build when a copy drifts.
//
// WHY A COPY AND NOT A PACKAGE: frontend (Vite/vitest), mobile (Metro/Expo 54)
// and tv (Metro/Expo 56, react-native-tvos) are three independent npm projects
// with three toolchains and no workspace root. Wiring a cross-root package
// would mean Metro watchFolders, resolver paths and a second tsconfig project
// in each of them — far more machinery than the ~100 lines below. The contract
// is what has to be shared; the enforcement is the byte-identity check.
//
// ── THE CONTRACT ───────────────────────────────────────────────────────────
// Every consumer probes the AUTHENTICATED /video URL with `Range: bytes=0-0`
// and turns the response head into ONE verdict. Only auth, the concrete fetch,
// cancellation, the player runtime and the error/preparing UX may differ
// between consumers; the classification below may not.
//
//   202                       → preparing (Retry-After is a FLOOR, not a date)
//   200 / 206                 → ready; the MIME only says HLS vs progressive
//   404                       → not-found
//   401 / 403                 → auth-error
//   408 / 425 / 429 / 5xx     → transient-error
//   anything else             → protocol-error
//
// The MIME is a DISCRIMINATOR, never a playability gate. The server has
// already authorized and classified the content before it serves 200/206, so
// `206 + application/octet-stream`, `206 + video/quicktime` and `206` with no
// Content-Type at all are the same thing: a playable progressive stream. The
// older mobile rule ("206 + video/* or it is unavailable") turned real videos
// into a permanent error whenever React Native handed back a head without the
// type, and had no counterpart on web or TV.

/** The exact HLS MIME NubArca's VideoHlsServingService declares. */
export const HLS_MIME = 'application/vnd.apple.mpegurl';

export type VideoDeliveryMode = 'hls' | 'progressive';

/**
 * What one probed response means. Deliberately NOT collapsed into a single
 * "unavailable": a missing file, an expired session, a temporary boundary and
 * an unexpected status need different handling even when a given consumer
 * chooses to draw them the same way.
 */
export type VideoDeliveryVerdict =
  | { kind: 'ready'; mode: VideoDeliveryMode }
  | { kind: 'preparing'; retryAfterMs: number | null }
  | { kind: 'not-found' }
  | { kind: 'auth-error' }
  | { kind: 'transient-error' }
  | { kind: 'protocol-error' };

export type VideoDeliveryVerdictKind = VideoDeliveryVerdict['kind'];

/**
 * Is this Content-Type the HLS master playlist?
 *
 * Case-insensitive, and parameters after ';' are ignored: ASP.NET Core
 * materializes `Results.Text` as UTF-8, so a ready ladder really does arrive
 * as `application/vnd.apple.mpegurl; charset=utf-8` on a live server.
 */
export function isHlsMime(contentType: string | null | undefined): boolean {
  if (contentType === null || contentType === undefined) return false;
  const semicolon = contentType.indexOf(';');
  const bare = semicolon === -1 ? contentType : contentType.slice(0, semicolon);
  return bare.trim().toLowerCase() === HLS_MIME;
}

/** Pure verdict for one probed response head. */
export function classifyVideoDelivery(
  status: number,
  contentType: string | null | undefined,
  retryAfter?: string | null,
): VideoDeliveryVerdict {
  if (status === 202) {
    return { kind: 'preparing', retryAfterMs: parseRetryAfterMs(retryAfter) };
  }
  if (status === 200 || status === 206) {
    return { kind: 'ready', mode: isHlsMime(contentType) ? 'hls' : 'progressive' };
  }
  if (status === 401 || status === 403) return { kind: 'auth-error' };
  if (status === 404) return { kind: 'not-found' };
  if (status === 408 || status === 425 || status === 429) {
    return { kind: 'transient-error' };
  }
  if (status >= 500 && status <= 599) return { kind: 'transient-error' };
  return { kind: 'protocol-error' };
}

/**
 * The verdict for a probe that never produced a response head at all — a
 * dropped connection, a DNS failure, a per-attempt timeout. A network boundary
 * is not evidence that the media is missing, so it classifies exactly like a
 * 5xx and gets the same bounded retry.
 */
export function transportFailureVerdict(): VideoDeliveryVerdict {
  return { kind: 'transient-error' };
}

// ── RETRY POLICY ───────────────────────────────────────────────────────────

/** Delays between successive 202 probes, in ms. Never exceeds the last entry. */
export const PREPARATION_BACKOFF_MS = [1500, 2500, 5000] as const;

/** Bound on a server-supplied Retry-After, so a bad header cannot stall us. */
export const MAX_RETRY_AFTER_MS = 30_000;

/**
 * How many times a transient boundary is re-probed before it is surfaced.
 *
 * Unlike a 202 — where the server is actively telling us work is in progress —
 * nothing here says progress is being made, so this budget is finite. It is
 * shared so a flaky-network retry does not become a mobile-only behaviour.
 */
export const TRANSIENT_MAX_RETRIES = 3;

/**
 * Parse a `Retry-After` header.
 *
 * HTTP allows both delta-seconds and an HTTP-date; only the numeric form is
 * honoured here, because the date form needs a trusted clock and this endpoint
 * only ever produces the numeric one. Anything unparseable, negative or
 * non-finite returns null so the caller falls back to its own schedule.
 */
export function parseRetryAfterMs(header: string | null | undefined): number | null {
  if (header === null || header === undefined) return null;
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
export function preparationDelayMs(attempt: number, retryAfterMs: number | null): number {
  return Math.max(backoffStepMs(attempt), retryAfterMs ?? 0);
}

/** `preparationDelayMs` straight from the raw header. */
export function nextPreparationDelayMs(
  attempt: number,
  retryAfterHeader?: string | null,
): number {
  return preparationDelayMs(attempt, parseRetryAfterMs(retryAfterHeader));
}

// ── POLL LOOP ──────────────────────────────────────────────────────────────

/** The two independent retry counters a probe loop carries. */
export interface VideoDeliveryPollState {
  readonly preparingAttempt: number;
  readonly transientAttempt: number;
}

export const INITIAL_POLL_STATE: VideoDeliveryPollState = {
  preparingAttempt: 0,
  transientAttempt: 0,
};

/**
 * What a consumer does next with a verdict.
 *
 * `surface: 'preparing'` means the caller SHOULD show its preparing state
 * before waiting — a 202 can last a whole transcode and must not hide behind a
 * spinner. `surface: 'silent'` is a transient blip being retried: the caller
 * keeps whatever it is already showing, so a single flaky request never
 * flashes an error the retry is about to clear.
 */
export type VideoDeliveryPlan =
  | { action: 'settle'; verdict: VideoDeliveryVerdict }
  | {
      action: 'retry';
      delayMs: number;
      surface: 'preparing' | 'silent';
      state: VideoDeliveryPollState;
    };

/**
 * The ONE retry policy, shared by web, mobile and TV.
 *
 * A 202 is retried FOREVER. A long but healthy transcode must not become an
 * error just because a consumer reached an arbitrary attempt count; the probe
 * ends when it turns ready, when a terminal verdict arrives, or when the
 * caller cancels (unmount, navigation, logout). Each individual request stays
 * time-bounded by its consumer — that is a request timeout, not a loop budget.
 *
 * A transient boundary is retried TRANSIENT_MAX_RETRIES times on the same ramp
 * and then settles, so the UI can offer a retry action instead of spinning.
 * Reaching a 202 resets that budget: the connection demonstrably works again.
 */
export function planNextProbe(
  verdict: VideoDeliveryVerdict,
  state: VideoDeliveryPollState = INITIAL_POLL_STATE,
): VideoDeliveryPlan {
  if (verdict.kind === 'preparing') {
    return {
      action: 'retry',
      delayMs: preparationDelayMs(state.preparingAttempt, verdict.retryAfterMs),
      surface: 'preparing',
      state: { preparingAttempt: state.preparingAttempt + 1, transientAttempt: 0 },
    };
  }
  if (verdict.kind === 'transient-error' && state.transientAttempt < TRANSIENT_MAX_RETRIES) {
    return {
      action: 'retry',
      delayMs: backoffStepMs(state.transientAttempt),
      surface: 'silent',
      state: {
        preparingAttempt: state.preparingAttempt,
        transientAttempt: state.transientAttempt + 1,
      },
    };
  }
  return { action: 'settle', verdict };
}

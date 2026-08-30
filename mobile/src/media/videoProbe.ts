// Mobile video preflight probe — the React Native adapter over NubArca's
// canonical /video delivery contract (media/videoDelivery.ts).
//
// WHAT IS SHARED AND WHAT IS NOT (VIDEO-DELIVERY-PARITY-01): the status
// classification, the HLS-vs-progressive discrimination, the Retry-After
// parsing and the retry policy all live in videoDelivery.ts and are
// byte-identical to the copies web and TV use. This file owns only what is
// genuinely React-Native-specific:
//   * the session Cookie header (RN has no shared cookie jar for fetch);
//   * a FRESH AbortController per attempt, aborted the moment the response
//     head is known, so a Range-enabled ORIGINAL is never streamed into the
//     probe (with Media__VideoHlsProvider off, /video IS the original);
//   * reading status + Content-Type into plain JS values BEFORE that abort —
//     RN owns the Response implementation and may release its native header
//     accessor once the request is aborted, which used to turn a real
//     `200 application/vnd.apple.mpegurl` into `200` with no MIME;
//   * a per-attempt wall-clock bound, so a black-holed connection cannot hold
//     one attempt (and therefore the probe) forever;
//   * caller cancellation: aborting the caller signal kills the active request
//     AND collapses the pending retry delay, with no further attempts.
//
// WHAT THIS FILE DELIBERATELY NO LONGER DOES:
//   * it does not gate playability on the MIME. 200/206 is playable; the MIME
//     only says HLS or progressive. `206 + application/octet-stream` and a 206
//     with no Content-Type at all are progressive video, not "unavailable" —
//     the old mobile-only rule made real videos permanently unplayable on
//     device whenever the head arrived without the type.
//   * it does not cap "preparing" at ten attempts. A long but healthy
//     transcode is not an error; the loop ends on ready, on a terminal
//     verdict, or when the caller cancels.
//
// The caller resolves the expo-video source FROM THE OUTCOME: shared media
// keeps its server-provided album-scoped URL unchanged; the probed URL is the
// played URL. A different media URL is never synthesized.

import {
  INITIAL_POLL_STATE,
  classifyVideoDelivery,
  planNextProbe,
  transportFailureVerdict,
  type VideoDeliveryMode,
  type VideoDeliveryPollState,
  type VideoDeliveryVerdict,
} from './videoDelivery.ts';

export type {
  VideoDeliveryMode,
  VideoDeliveryVerdict,
} from './videoDelivery.ts';

export const VIDEO_PROBE_RANGE = 'bytes=0-0';
// Wall-clock bound for ONE network attempt. Generous — real servers answer
// heads in well under a second — but finite, so a black-holed connection can
// never hold an attempt indefinitely. This is a REQUEST timeout, not a loop
// budget: a timed-out attempt is a transient failure and follows the shared
// transient retry policy like any other.
export const VIDEO_PROBE_ATTEMPT_TIMEOUT_MS = 5000;

/** The container the player must be told about. */
export type VideoContainer = VideoDeliveryMode;

export interface VideoProbeSource {
  uri: string;
  headers: { cookie: string };
}

/**
 * The settled probe result: a canonical delivery verdict, or `cancelled`.
 *
 * Cancellation is its OWN outcome. Folding it into "unavailable" made an
 * unmount indistinguishable from a missing file, and a caller that raced the
 * teardown could paint an error for a video that was perfectly fine.
 */
export type VideoProbeOutcome = VideoDeliveryVerdict | { kind: 'cancelled' };

export interface ExpoVideoSource {
  uri: string;
  headers: { cookie: string };
  // ALWAYS declared, for progressive as much as for HLS: ExoPlayer cannot
  // infer a container from an extension-less /video URL, and omitting the hint
  // left it guessing on exactly the sources this probe had already identified.
  contentType: VideoDeliveryMode;
}

export interface ProbeResponseLike {
  status: number;
  headers?:
    | { get(name: string): string | null }
    | Record<string, string | string[] | undefined>;
}

export type VideoProbeFetch = (
  uri: string,
  init: { headers: Record<string, string>; signal: AbortSignal },
) => Promise<ProbeResponseLike>;

export interface VideoProbeDeps {
  fetchImpl?: VideoProbeFetch;
  /**
   * Observes the "still preparing" verdict AS IT HAPPENS. The promise settles
   * only on the terminal outcome, so without this callback a 202 stays
   * invisible behind the retry loop and the caller cannot show its preparing
   * state at all. Fired before every preparing wait.
   */
  onPreparing?: () => void;
  /**
   * CALLER cancellation: aborting this signal terminates the WHOLE probe
   * immediately — the active attempt is aborted through its own controller and
   * the pending retry delay collapses. Deliberately distinct from
   * attemptTimeoutMs, which only fails one attempt.
   */
  signal?: AbortSignal;
  /** Per-attempt wall-clock bound. Defaults to VIDEO_PROBE_ATTEMPT_TIMEOUT_MS. */
  attemptTimeoutMs?: number;
  /**
   * The retry wait. Injected ONLY so tests can assert the canonical backoff
   * schedule without sleeping through it; production always uses the real
   * abortable sleep, and the delays themselves come from the shared policy.
   */
  sleepImpl?: (ms: number, signal?: AbortSignal) => Promise<void>;
}

function linkCallerAbort(
  caller: AbortSignal | undefined,
  controller: AbortController,
): () => void {
  if (!caller) return () => {};
  if (caller.aborted) {
    controller.abort();
    return () => {};
  }
  const onAbort = () => controller.abort();
  caller.addEventListener('abort', onAbort);
  return () => caller.removeEventListener('abort', onAbort);
}

/** Retry delay that collapses IMMEDIATELY when the caller cancels — a plain
 * `setTimeout` sleep would keep waiting through unmount/logout. */
export function abortableSleep(ms: number, signal?: AbortSignal): Promise<void> {
  return new Promise((resolve) => {
    if (signal?.aborted) {
      resolve();
      return;
    }
    let timer: ReturnType<typeof setTimeout> | undefined;
    const done = () => {
      if (timer !== undefined) clearTimeout(timer);
      signal?.removeEventListener('abort', done);
      resolve();
    };
    timer = setTimeout(done, ms);
    signal?.addEventListener('abort', done);
  });
}

function readHeader(res: ProbeResponseLike, name: string): string | null {
  const headers = res.headers;
  if (headers === undefined) return null;
  const maybeGet = (headers as { get?: unknown }).get;
  if (typeof maybeGet === 'function') {
    return (headers as { get(n: string): string | null }).get(name);
  }
  const bag = headers as Record<string, string | string[] | undefined>;
  for (const key of Object.keys(bag)) {
    if (key.toLowerCase() !== name.toLowerCase()) continue;
    const value = bag[key];
    if (Array.isArray(value)) return value[0] ?? null;
    return value ?? null;
  }
  return null;
}

/**
 * Run the preflight against ONE source. Fresh AbortController per attempt;
 * each attempt is aborted right after the response head is known; the shared
 * policy (planNextProbe) decides what happens next.
 */
export async function probeVideoSource(
  source: VideoProbeSource,
  deps: VideoProbeDeps = {},
): Promise<VideoProbeOutcome> {
  const doFetch: VideoProbeFetch =
    deps.fetchImpl ??
    ((uri, init) => fetch(uri, init as RequestInit) as unknown as Promise<ProbeResponseLike>);
  const sleep = deps.sleepImpl ?? abortableSleep;
  const attemptTimeoutMs =
    deps.attemptTimeoutMs ?? VIDEO_PROBE_ATTEMPT_TIMEOUT_MS;
  const caller = deps.signal;
  let poll: VideoDeliveryPollState = INITIAL_POLL_STATE;

  // Unbounded on purpose: only a terminal verdict or the CALLER ends this loop
  // (see planNextProbe). Preparing has no attempt ceiling; every other verdict
  // settles within the shared transient budget.
  for (;;) {
    // Caller already gone (e.g. cancelled during the retry delay): nothing
    // further may start.
    if (caller?.aborted) return { kind: 'cancelled' };

    const controller = new AbortController();
    let unlinkCaller: () => void = () => {};
    let timer: ReturnType<typeof setTimeout> | undefined;
    let verdict: VideoDeliveryVerdict;

    try {
      // Caller cancellation aborts THIS ATTEMPT'S controller — the fetch
      // itself dies, not just the surrounding await.
      unlinkCaller = linkCallerAbort(caller, controller);
      timer = setTimeout(() => controller.abort(), attemptTimeoutMs);

      const res = await doFetch(source.uri, {
        headers: { cookie: source.headers.cookie, range: VIDEO_PROBE_RANGE },
        signal: controller.signal,
      });
      // Snapshot the response head BEFORE aborting (see the file header).
      const status = res.status;
      const contentType = readHeader(res, 'content-type');
      const retryAfter = readHeader(res, 'retry-after');
      // The head is now owned by plain JS values: stop the body transfer.
      controller.abort();
      verdict = classifyVideoDelivery(status, contentType, retryAfter);
    } catch {
      // Only a CALLER-triggered abort may terminate the whole probe from here.
      // A per-attempt timeout or a transport failure is a transient boundary
      // and gets the shared bounded retry, never a false "not found".
      if (caller?.aborted) return { kind: 'cancelled' };
      verdict = transportFailureVerdict();
    } finally {
      // Timers and listeners are released on EVERY exit path.
      if (timer !== undefined) clearTimeout(timer);
      unlinkCaller();
    }

    const plan = planNextProbe(verdict, poll);
    if (plan.action === 'settle') return plan.verdict;
    // There IS going to be a wait: tell the caller now instead of hiding the
    // preparing state until the loop finally settles. A silently retried
    // transient blip leaves the current presentation alone.
    if (plan.surface === 'preparing') deps.onPreparing?.();
    poll = plan.state;

    // The wait before the next attempt must not outlive its caller either.
    await sleep(plan.delayMs, caller);
    if (caller?.aborted) return { kind: 'cancelled' };
  }
}

/**
 * Probe under a manager-owned AbortController — exactly the handle a screen
 * effect needs: `cancel()` (cleanup/unmount) kills the in-flight attempt and
 * any pending retry delay at once, and the settled outcome is safe to ignore
 * because a cancelled probe resolves to `cancelled`, which mounts nothing.
 */
export function createManagedProbe(
  source: VideoProbeSource,
  deps: Omit<VideoProbeDeps, 'signal'> = {},
): { cancel(): void; outcome: Promise<VideoProbeOutcome> } {
  const controller = new AbortController();
  const outcome = probeVideoSource(source, { ...deps, signal: controller.signal });
  return { cancel: () => controller.abort(), outcome };
}

/**
 * Resolve the expo-video source FROM the probe outcome: null unless ready.
 * BOTH containers declare contentType, and the media URL is always exactly the
 * probed one — never synthesized. Shared-album playback therefore keeps its
 * server-provided album-scoped URL and never falls back to an owner route.
 */
export function resolveExpoVideoSource(
  base: VideoProbeSource,
  outcome: VideoProbeOutcome,
): ExpoVideoSource | null {
  if (outcome.kind !== 'ready') return null;
  return { uri: base.uri, headers: base.headers, contentType: outcome.mode };
}

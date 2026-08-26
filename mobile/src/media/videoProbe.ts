// Video preflight probe (acceptance contract fix).
//
// WHY NOT AN ORDINARY GET: with Media__VideoHlsProvider disabled the owned
// /api/files/{id}/video route IS the Range-enabled ORIGINAL — a plain GET
// would start streaming the entire file into the probe. With the provider on,
// the SAME extensionless route becomes an HLS master playlist that answers
// 202 while the ladder prepares; shared /video is HLS-only by contract.
// The probe therefore:
//   * sends `Range: bytes=0-0` next to the exact session Cookie — a compliant
//     server answers 206 with ONE byte; a non-compliant one answers 200, and
//     the request is ABORTED the moment status + Content-Type are known, so a
//     body is never buffered either way;
//   * uses a FRESH AbortController per attempt and never touches res.body;
//   * bounds EVERY attempt with attemptTimeoutMs — a fetch whose head never
//     arrives cannot hold the probe forever (the timeout counts as ONE failed
//     attempt inside the existing budget, it does not end the probe);
//   * observes an optional caller signal: aborting it kills the active request
//     AND the retry delay immediately, with NO further attempts;
//   * retries only the "ladder still preparing" verdict (202), under a bounded
//     attempt/retry budget, reporting each such verdict through onPhase so the
//     caller can surface "preparing" while the loop is still running;
//
// Classification follows NubArca's real contracts, not URL shapes:
//   202                                   → preparing (retry)
//   404 / other deliberate status         → unavailable
//   206 + Content-Type video/*            → ready PROGRESSIVE (native original)
//   200 + application/vnd.apple.mpegurl   → ready HLS (parameters after ';'
//                                           are ignored — see below)
//
// The caller resolves the expo-video source FROM THE OUTCOME: shared media
// keeps its server-provided album-scoped URL unchanged; owned media gains
// contentType:'hls' only when the server said HLS. A different media URL is
// never synthesized.

export const VIDEO_PROBE_RANGE = 'bytes=0-0';
export const VIDEO_PROBE_RETRY_MS = 3000;
export const VIDEO_PROBE_MAX_ATTEMPTS = 10;
// Wall-clock bound for ONE network attempt. Generous — real servers answer
// heads in well under a second — but finite, so a black-holed connection can
// never hold an attempt (and therefore the probe) indefinitely.
export const VIDEO_PROBE_ATTEMPT_TIMEOUT_MS = 5000;

/** The exact HLS MIME NubArca's VideoHlsServingService declares. */
export const NUBARCA_HLS_MIME = 'application/vnd.apple.mpegurl';

export type VideoContainer = 'hls' | 'progressive';
export type VideoProbePhase = 'ready' | 'preparing' | 'unavailable';

export interface VideoProbeSource {
  uri: string;
  headers: { cookie: string };
}

export interface VideoProbeOutcome {
  phase: VideoProbePhase;
  container?: VideoContainer;
}

export interface ExpoVideoSource {
  uri: string;
  headers: { cookie: string };
  contentType?: 'hls';
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
  retryMs?: number;
  maxAttempts?: number;
  /**
   * Observes the transient "still preparing" verdict AS IT HAPPENS. The
   * promise settles only on the terminal outcome, so without this callback a
   * 202 stays invisible behind the internal retry loop and the caller cannot
   * show its preparing state at all. Fired at most once per retried 202 (the
   * budget-exhausting one resolves straight to unavailable); terminal phases
   * are delivered through the promise instead.
   */
  onPhase?: (phase: VideoProbePhase) => void;
  /**
   * CALLER cancellation (MOBILE-VIDEO-PROBE-LIFECYCLE-01): aborting this
   * signal terminates the WHOLE probe immediately — the active attempt is
   * aborted through its own controller and the pending retry delay collapses.
   * Deliberately distinct from attemptTimeoutMs, which only fails one attempt.
   */
  signal?: AbortSignal;
  /**
   * Per-attempt wall-clock bound: an attempt whose HEAD has not arrived within
   * this budget gets its signal aborted and counts as ONE failed attempt (the
   * normal retry budget still applies). Defaults to
   * VIDEO_PROBE_ATTEMPT_TIMEOUT_MS, so no network attempt waits indefinitely.
   */
  attemptTimeoutMs?: number;
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
function abortableSleep(ms: number, signal?: AbortSignal): Promise<void> {
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

/** Pure verdict for one probed response. */
export function classifyVideoProbe(
  status: number,
  contentType: string | null,
): VideoProbeOutcome {
  if (status === 202) return { phase: 'preparing' };

  if (status === 206) {
    // Partial content of a REAL media stream: native progressive playback.
    const ct = (contentType ?? '').trim();
    if (/^video\//i.test(ct)) return { phase: 'ready', container: 'progressive' };
    return { phase: 'unavailable' }; // ranged something that is not video
  }

  if (status === 200) {
    // Compare ONLY the bare MIME type: ASP.NET Core materializes Results.Text
    // responses as UTF-8 text, so the declared master type can arrive as
    // "application/vnd.apple.mpegurl; charset=utf-8". RFC 9110 media types
    // carry parameters after ';', and an exact match would downgrade a READY
    // ladder to unavailable on a real server.
    const mime = (contentType ?? '')
      .split(';', 1)[0]
      .trim()
      .toLowerCase();
    if (mime === NUBARCA_HLS_MIME) return { phase: 'ready', container: 'hls' };
    const ctRaw = (contentType ?? '').trim();
    if (/^video\//i.test(ctRaw)) {
      // A 200 that ignored our single-byte range but IS native video: usable
      // progressively — and the request was already aborted at header time,
      // so the full body was never pulled into the client.
      return { phase: 'ready', container: 'progressive' };
    }
  }

  // 404 / 403 / anything else deliberate: no playback through this route.
  return { phase: 'unavailable' };
}

/**
 * Run the bounded preflight against ONE source. Fresh AbortController per
 * attempt; each attempt is aborted right after the response head is known.
 * Only 202 keeps the loop alive (bounded by maxAttempts).
 */
export async function probeVideoSource(
  source: VideoProbeSource,
  deps: VideoProbeDeps = {},
): Promise<VideoProbeOutcome> {
  const doFetch: VideoProbeFetch =
    deps.fetchImpl ??
    ((uri, init) => fetch(uri, init as RequestInit) as unknown as Promise<ProbeResponseLike>);
  const retryMs = deps.retryMs ?? VIDEO_PROBE_RETRY_MS;
  const maxAttempts = deps.maxAttempts ?? VIDEO_PROBE_MAX_ATTEMPTS;
  const attemptTimeoutMs =
    deps.attemptTimeoutMs ?? VIDEO_PROBE_ATTEMPT_TIMEOUT_MS;
  const caller = deps.signal;

  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    // Caller already gone (e.g. cancelled during the retry delay): nothing
    // further may start.
    if (caller?.aborted) return { phase: 'unavailable' };

    const controller = new AbortController();
    let unlinkCaller: () => void = () => {};
    let timer: ReturnType<typeof setTimeout> | undefined;

    try {
      // Caller cancellation aborts THIS ATTEMPT'S controller — the fetch
      // itself dies, not just the surrounding await.
      unlinkCaller = linkCallerAbort(caller, controller);
      timer = setTimeout(() => controller.abort(), attemptTimeoutMs);

      const res = await doFetch(source.uri, {
        headers: { cookie: source.headers.cookie, range: VIDEO_PROBE_RANGE },
        signal: controller.signal,
      });
      // Head of the response is known: stop the transfer immediately.
      controller.abort();
      const outcome = classifyVideoProbe(res.status, readHeader(res, 'content-type'));
      if (outcome.phase !== 'preparing') return outcome;
      if (attempt >= maxAttempts) return { phase: 'unavailable' };
      // There IS going to be a wait: tell the caller now instead of hiding
      // the preparing state until the loop finally settles.
      deps.onPhase?.(outcome.phase);
    } catch {
      // WHY did this attempt die? Only a CALLER-triggered abort may terminate
      // the whole probe from here:
      //   * caller abort         → stop NOW: no retry, no delay;
      //   * per-attempt timeout  → one FAILED attempt, budget still applies;
      //   * transport failure    → existing retry behaviour.
      if (caller?.aborted) return { phase: 'unavailable' };
      if (attempt >= maxAttempts) return { phase: 'unavailable' };
    } finally {
      // Timers and listeners are released on EVERY exit path.
      if (timer !== undefined) clearTimeout(timer);
      unlinkCaller();
    }

    // The wait before the next attempt must not outlive its caller either.
    await abortableSleep(retryMs, caller);
  }
  return { phase: 'unavailable' };
}

/**
 * Probe under a manager-owned AbortController — exactly the handle a screen
 * effect needs: `cancel()` (cleanup/unmount) kills the in-flight attempt and
 * any pending retry delay at once, and the settled outcome is safe to ignore
 * because a cancelled probe resolves to a non-mountable verdict anyway.
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
 * Resolve the expo-video source FROM the probe outcome: null unless ready;
 * HLS gets the explicit container declaration, progressive stays native.
 * The media URL is always exactly the probed one — never synthesized.
 */
export function resolveExpoVideoSource(
  base: VideoProbeSource,
  outcome: VideoProbeOutcome,
): ExpoVideoSource | null {
  if (outcome.phase !== 'ready' || outcome.container === undefined) return null;
  if (outcome.container === 'hls') {
    return { uri: base.uri, headers: base.headers, contentType: 'hls' as const };
  }
  return { uri: base.uri, headers: base.headers };
}

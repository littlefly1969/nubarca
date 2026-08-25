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
}

function sleep(ms: number): Promise<void> {
  return new Promise((r) => setTimeout(r, ms));
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

  for (let attempt = 1; attempt <= maxAttempts; attempt += 1) {
    const controller = new AbortController();
    try {
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
      await sleep(retryMs);
    } catch {
      // Transport-level failure mid-probe behaves like "still preparing" and
      // consumes one attempt, keeping the bounded budget honest.
      if (attempt >= maxAttempts) return { phase: 'unavailable' };
      await sleep(retryMs);
    }
  }
  return { phase: 'unavailable' };
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

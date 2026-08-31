// Classify what GET /api/tv/media/{id}/video will serve.
// The endpoint speaks two contracts depending on the server's
// Media:VideoHlsProvider flag:
//   - adaptive:  200 `application/vnd.apple.mpegurl` master (ladder ready) or
//                202 while the transcode is being prepared (the request itself
//                idempotently enqueues the generation job)
//   - legacy:    the Range-enabled progressive byte stream (206 for our
//                1-byte probe)
// The player needs the distinction up front: ExoPlayer cannot infer HLS from
// a URL without an .m3u8 extension, so the mode selects the explicit
// expo-video `contentType` hint ('hls' vs 'progressive').
//
// The CLASSIFICATION is not TV-specific and does not live here: it is the
// canonical contract in videoDelivery.ts, shared byte-for-byte with the web
// and mobile clients (VIDEO-DELIVERY-PARITY-01). What is TV-specific is the
// /api/tv boundary and the TV session headers below.

import { getTvMediaHeaders, resolveTvMediaUrl } from '../api/client';
import { classifyVideoDelivery, transportFailureVerdict } from './videoDelivery';
import type { VideoDeliveryVerdict } from './videoDelivery';
import { tvVideoModeFor, type TvVideoMode } from './probeClassify';

export type { TvVideoMode };
export { tvVideoModeFor };

/** Probe once and return the CANONICAL verdict; the caller owns the retry loop. */
export async function probeTvVideoDelivery(
  path: string,
  personal = false,
): Promise<VideoDeliveryVerdict> {
  let url: string;
  try {
    url = resolveTvMediaUrl(path); // enforces the /api/tv boundary
  } catch {
    // A path this client refuses to resolve is a client-side contract break,
    // not a temporary condition: it must not be retried.
    return { kind: 'protocol-error' };
  }
  try {
    const res = await fetch(url, {
      headers: { ...getTvMediaHeaders(personal), Range: 'bytes=0-0' },
      credentials: 'omit',
    });
    return classifyVideoDelivery(
      res.status,
      res.headers.get('content-type'),
      res.headers.get('retry-after'),
    );
  } catch {
    // No response head at all: a network boundary, never evidence of a
    // missing file. The shared policy retries it a bounded number of times.
    return transportFailureVerdict();
  }
}

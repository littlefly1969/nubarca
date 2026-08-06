// Video-hls slice 4: classify what GET /api/tv/media/{id}/video will serve.
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

import { getTvMediaHeaders, resolveTvMediaUrl } from '../api/client';
import { classifyVideoProbe, type TvVideoMode } from './probeClassify';

export type { TvVideoMode };

export async function probeTvVideo(path: string, personal = false): Promise<TvVideoMode> {
  let url: string;
  try {
    url = resolveTvMediaUrl(path); // enforces the /api/tv boundary
  } catch {
    return 'error';
  }
  try {
    const res = await fetch(url, {
      headers: { ...getTvMediaHeaders(personal), Range: 'bytes=0-0' },
      credentials: 'include',
    });
    return classifyVideoProbe(res.status, res.headers.get('content-type'));
  } catch {
    return 'error';
  }
}

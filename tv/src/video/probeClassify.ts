// TV adapter over NubArca's canonical /video delivery contract
// (src/video/videoDelivery.ts — byte-identical to the copies frontend and
// mobile use; see that file's header). No React, no native imports, so
// node --test covers it directly. The fetch wrapper lives in probe.ts.
//
// The TV screen draws every terminal non-ready verdict the same way (poster +
// error pill, the item still navigable). That is a PRESENTATION choice and is
// allowed to differ per consumer; the transport verdicts themselves stay
// distinct and identical across web, mobile and TV.

import {
  classifyVideoDelivery,
  type VideoDeliveryVerdict,
} from './videoDelivery.ts';

export type TvVideoMode = 'hls' | 'direct' | 'preparing' | 'error';

/** Canonical verdict → what this screen renders. */
export function tvVideoModeFor(verdict: VideoDeliveryVerdict): TvVideoMode {
  switch (verdict.kind) {
    case 'ready':
      // 'direct' is this screen's historical name for progressive playback.
      return verdict.mode === 'hls' ? 'hls' : 'direct';
    case 'preparing':
      return 'preparing';
    case 'not-found':
    case 'auth-error':
    case 'transient-error':
    case 'protocol-error':
      return 'error';
  }
}

/** Classify one probed response head straight into the TV mode. */
export function classifyVideoProbe(status: number, contentType: string | null): TvVideoMode {
  return tvVideoModeFor(classifyVideoDelivery(status, contentType));
}

// The WEB adapter over NubArca's canonical /video delivery contract
// (videoDelivery.ts, byte-identical to the copies mobile and TV use).
//
// It lives in its own module — with no React, no hls.js and no DOM — for the
// same reason the TV and mobile adapters do: the cross-consumer parity test
// has to be able to load all three side by side and prove they agree row for
// row (VIDEO-DELIVERY-PARITY-01). Importing the player component would drag in
// the whole viewer.

import type { VideoDeliveryVerdict } from './videoDelivery';

/** What HlsVideoPlayer renders. 'direct' is its historical name for progressive. */
export type VideoPlaybackMode = 'preparing' | 'hls' | 'direct' | 'error';

/**
 * Canonical verdict → rendered mode.
 *
 * not-found, auth-error, transient-error and protocol-error stay DISTINCT
 * verdicts at transport level; this viewer simply draws all four the same way.
 * Collapsing them here is a presentation choice each consumer is free to make
 * differently — collapsing them in the classifier is what used to make the
 * three clients disagree about what /video had actually said.
 */
export function videoPlaybackModeFor(verdict: VideoDeliveryVerdict): VideoPlaybackMode {
  if (verdict.kind === 'ready') return verdict.mode === 'hls' ? 'hls' : 'direct';
  if (verdict.kind === 'preparing') return 'preparing';
  return 'error';
}

import { overscan } from '../theme.ts';
import type { Viewport } from './panelLayout';

// Pure geometry for the pairing landing page. React Native reports TV sizes in
// density-independent pixels, so a 1080p Fire Stick commonly arrives here as
// 960x540 rather than 1920x1080. That compact viewport is the important one:
// the old vertical stack was taller than the screen and clipped the lockup at
// the top even though the source PNG itself was sound.

const LOCKUP_WIDTH = 1280;
const LOCKUP_HEIGHT = 297;
const MAX_LOCKUP_WIDTH = 640;
const MAX_QR_SIZE = 320;
export const MIN_READABLE_PAIRING_QR = 180;

export const TV_PAIRING_VIEWPORTS: readonly Viewport[] = [
  // 1280x720 output at Android's common 2x TV density.
  { width: 640, height: 360 },
  // 1920x1080 output at 2x.
  { width: 960, height: 540 },
  // Density-1 / emulator and direct-resolution variants.
  { width: 1280, height: 720 },
  { width: 1920, height: 1080 },
];

export interface PairingLayout {
  readonly dense: boolean;
  readonly insetX: number;
  readonly insetY: number;
  readonly usableWidth: number;
  readonly usableHeight: number;
  readonly lockupWidth: number;
  readonly lockupHeight: number;
  readonly contentGap: number;
  readonly qrSize: number;
  readonly detailsWidth: number;
  readonly contentHeight: number;
}

export function pairingLayout(viewport: Viewport): PairingLayout {
  const inset = overscan(viewport.width, viewport.height);
  const usableWidth = Math.max(0, viewport.width - inset.x * 2);
  const usableHeight = Math.max(0, viewport.height - inset.y * 2);
  const dense = viewport.height <= 400;
  const contentGap = dense ? 12 : viewport.height <= 540 ? 20 : 32;

  // The approved raster is 1280x297. Deriving height from that master ratio is
  // what prevents either stretching or a hand-maintained second geometry.
  const heightBoundWidth = usableHeight * (dense ? 0.17 : 0.3) * LOCKUP_WIDTH / LOCKUP_HEIGHT;
  const lockupWidth = Math.round(Math.min(
    MAX_LOCKUP_WIDTH,
    usableWidth * 0.55,
    heightBoundWidth,
  ));
  const lockupHeight = Math.round(lockupWidth * LOCKUP_HEIGHT / LOCKUP_WIDTH);

  // Pairing details sit BESIDE the QR, so the QR — not the sum of every text
  // line — owns the row height. Bound it by both remaining height and its share
  // of the row width. The supported TV viewports all remain comfortably above
  // MIN_QR_SIZE; tests enforce that readability gate without forcing an
  // overflow in an arbitrarily small development window.
  const rowHeight = Math.max(0, usableHeight - lockupHeight - contentGap);
  const qrWidthShare = Math.max(0, (usableWidth - contentGap) * 0.4);
  const availableQrSize = Math.min(MAX_QR_SIZE, rowHeight, qrWidthShare);
  const qrSize = Math.round(availableQrSize);

  return {
    dense,
    insetX: inset.x,
    insetY: inset.y,
    usableWidth,
    usableHeight,
    lockupWidth,
    lockupHeight,
    contentGap,
    qrSize,
    detailsWidth: Math.max(0, usableWidth - qrSize - contentGap),
    contentHeight: lockupHeight + contentGap + qrSize,
  };
}

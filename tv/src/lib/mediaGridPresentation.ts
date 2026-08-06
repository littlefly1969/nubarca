import { spacing } from '../theme';

// Preserve the established number of items selected for each justified row,
// but render a much tighter gap and give the recovered width to the previews.
export const MEDIA_GRID_PACKING_GAP = spacing.sm;
export const MEDIA_GRID_VISUAL_GAP = 4;

// A focused media tile grows by 5%. Four dp per side is enough at the row
// heights used by the TV surfaces without recreating the old oversized gutters.
export const MEDIA_GRID_FOCUS_BLEED = 4;

// Every native media wall uses the Party geometry. Keeping this calculation in
// one place prevents Gallery and Videos from drifting back to their historical
// fixed-column densities.
export function mediaGridTargetRowHeight(surfaceHeight: number): number {
  return Math.min(Math.max(Math.round(surfaceHeight * 0.18), 125), 185);
}

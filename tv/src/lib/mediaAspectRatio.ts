import type { TvAlbumItem } from '../api/tv';

// Pure aspect-ratio rules for the TV app, kept intentionally identical to the
// web gallery's `frontend/src/media/workspace/mediaAspectRatio.ts` (photo → 1:1,
// video → 16:9 fallbacks; clamp [0.35, 3.5]). The TV app is a separate package
// and MUST NOT import SPA sources, so the math is duplicated here — keep the two
// in sync. The tile shape is derived exclusively from the DTO dimensions, never
// from a loaded thumbnail/poster, so a tile keeps its shape from first paint.

export const PHOTO_FALLBACK_ASPECT_RATIO = 1;
export const VIDEO_FALLBACK_ASPECT_RATIO = 16 / 9;
export const MIN_ASPECT_RATIO = 0.35;
export const MAX_ASPECT_RATIO = 3.5;

export function normalizeTvMediaAspectRatio(
  width: number | null | undefined,
  height: number | null | undefined,
  fallback: number,
): number {
  if (
    width == null ||
    height == null ||
    !Number.isFinite(width) ||
    !Number.isFinite(height) ||
    width <= 0 ||
    height <= 0
  ) {
    return fallback;
  }
  return Math.min(Math.max(width / height, MIN_ASPECT_RATIO), MAX_ASPECT_RATIO);
}

export function getTvMediaAspectRatio(
  item: Pick<TvAlbumItem, 'mediaType' | 'width' | 'height'>,
): number {
  const fallback = item.mediaType === 'video'
    ? VIDEO_FALLBACK_ASPECT_RATIO
    : PHOTO_FALLBACK_ASPECT_RATIO;
  return normalizeTvMediaAspectRatio(item.width, item.height, fallback);
}

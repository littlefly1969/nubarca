import type { MediaItem } from '@nubarca/api-client';
import { VIDEO_TILE_ASPECT_RATIO } from '../mediaDerivativeSpec';

// Shared, framework-free aspect-ratio rules for the media wall. The tile shape
// is derived exclusively from the DTO's declared pixel dimensions — NEVER from
// the loaded thumbnail/poster — so a tile keeps its shape from first paint and
// never reflows when the image arrives (task §"Stabilità dei placeholder").

// A photo with no usable dimensions falls back to a square; a video falls back
// to the 16:9 poster/preview stage ratio. Everything else uses the real ratio,
// clamped to a prudent band so a pathological (extremely tall/wide) source
// cannot produce a degenerate row in the justified layout.
export const PHOTO_FALLBACK_ASPECT_RATIO = 1;
export const VIDEO_FALLBACK_ASPECT_RATIO = VIDEO_TILE_ASPECT_RATIO;

// Clamp band for real ratios. 0.35 ≈ a very tall 9:25 sliver; 3.5 ≈ an ultra-
// wide 2.35:1-plus panorama. Beyond these the layout is protected without
// meaningfully distorting any realistic photo or video.
export const MIN_ASPECT_RATIO = 0.35;
export const MAX_ASPECT_RATIO = 3.5;

export function normalizeAspectRatio(
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

// The shape a media item occupies in the wall. Photos and videos both use their
// real pixel ratio; only genuinely missing/invalid dimensions fall back (1:1 for
// photos, 16:9 for videos).
export function getMediaAspectRatio(item: MediaItem): number {
  const fallback = item.kind === 'video'
    ? VIDEO_FALLBACK_ASPECT_RATIO
    : PHOTO_FALLBACK_ASPECT_RATIO;

  return normalizeAspectRatio(item.width, item.height, fallback);
}

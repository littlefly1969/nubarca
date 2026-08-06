import type { TvAlbumItem } from '@nubarca/api-client';
import {
  normalizeAspectRatio,
  PHOTO_FALLBACK_ASPECT_RATIO,
  VIDEO_FALLBACK_ASPECT_RATIO,
} from '../media/workspace/mediaAspectRatio';
import {
  computeJustifiedRows,
  type JustifiedLayoutItem,
  type JustifiedLayoutRow,
} from '../media/layout/computeJustifiedRows';

// The browser /tv fallback reuses the SAME web layout primitives as the gallery
// (computeJustifiedRows) and the SAME aspect-ratio rules (normalizeAspectRatio +
// the shared clamp/fallbacks), so the Party grid is visually identical to the
// native TV app: real aspect ratios, photo→1:1 / video→16:9 fallbacks, no forced
// 16:9. This file only adapts the TvAlbumItem shape to those primitives.

export const TV_GRID_GAP = 12;

// 10-foot rows: taller than the desktop gallery so a couch-distance viewer sees
// large tiles (a handful per row) rather than a dense wall.
const TV_TARGET_ROW_HEIGHT = 260;
const TV_MIN_ROW_HEIGHT = 200;
const TV_MAX_ROW_HEIGHT = 360;

export function getTvMediaAspectRatio(
  item: Pick<TvAlbumItem, 'mediaType' | 'width' | 'height'>,
): number {
  const fallback = item.mediaType === 'video'
    ? VIDEO_FALLBACK_ASPECT_RATIO
    : PHOTO_FALLBACK_ASPECT_RATIO;
  return normalizeAspectRatio(item.width, item.height, fallback);
}

export function buildTvRows(items: TvAlbumItem[], containerWidth: number): JustifiedLayoutRow[] {
  const layoutItems: JustifiedLayoutItem[] = items.map((item, index) => ({
    id: item.id,
    originalIndex: index,
    aspectRatio: getTvMediaAspectRatio(item),
  }));
  return computeJustifiedRows(layoutItems, {
    containerWidth,
    gap: TV_GRID_GAP,
    targetRowHeight: TV_TARGET_ROW_HEIGHT,
    minRowHeight: TV_MIN_ROW_HEIGHT,
    maxRowHeight: TV_MAX_ROW_HEIGHT,
  });
}

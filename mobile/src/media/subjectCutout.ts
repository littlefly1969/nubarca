// Pure rules for "copy subject" in the mobile viewer.

import { filePreviewPath } from '../api/filePaths.ts';
import type { ViewerSlide } from './viewerSequence.ts';

/**
 * Only the owner's own photos. A slide is the owner's when its image comes
 * from the owner's preview route for its own key — exactly how owner slides
 * are built. A shared-album slide carries an album-scoped URL instead and its
 * key is an album item id, so it can never pass this test.
 */
export function canCutOutSlide(slide: Pick<ViewerSlide, 'key' | 'kind' | 'imagePath'>): boolean {
  return slide.kind === 'image' && slide.imagePath === filePreviewPath(slide.key);
}

/** "IMG_0001.HEIC" → "IMG_0001-cutout.png", safe to use as a file name. */
export function cutoutFileName(displayName: string): string {
  const base = displayName.slice(Math.max(displayName.lastIndexOf('/'), displayName.lastIndexOf('\\')) + 1);
  const dot = base.lastIndexOf('.');
  const stem = (dot >= 0 ? base.slice(0, dot) : base)
    .replace(/[^\p{L}\p{N} ._()-]/gu, '_')
    .trim();
  return `${stem === '' ? 'photo' : stem}-cutout.png`;
}

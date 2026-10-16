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

/** Width and height from a PNG's IHDR chunk, or null when it is not a PNG. */
export function pngSize(bytes: ArrayBuffer): { width: number; height: number } | null {
  if (bytes.byteLength < 24) return null;
  const view = new DataView(bytes);
  // Signature 89 50 4E 47, then the IHDR chunk: width and height big-endian.
  if (view.getUint32(0) !== 0x89504e47 || view.getUint32(12) !== 0x49484452) return null;
  const width = view.getUint32(16);
  const height = view.getUint32(20);
  return width > 0 && height > 0 ? { width, height } : null;
}

/**
 * A tap at (x, y) in a view that shows an image with resizeMode "contain", as
 * fractions of the IMAGE (0..1), or null when the tap lands on the empty bands
 * around it.
 */
export function containFraction(
  x: number,
  y: number,
  view: { width: number; height: number },
  image: { width: number; height: number },
): { fx: number; fy: number } | null {
  if (view.width <= 0 || view.height <= 0 || image.width <= 0 || image.height <= 0) return null;
  const scale = Math.min(view.width / image.width, view.height / image.height);
  const shownWidth = image.width * scale;
  const shownHeight = image.height * scale;
  const fx = (x - (view.width - shownWidth) / 2) / shownWidth;
  const fy = (y - (view.height - shownHeight) / 2) / shownHeight;
  return fx < 0 || fx > 1 || fy < 0 || fy > 1 ? null : { fx, fy };
}

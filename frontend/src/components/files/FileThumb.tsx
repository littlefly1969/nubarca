import { useState } from 'react';
import type { FileSummary } from '@nubarca/api-client';
import { isImage, mediaKindOf, smallThumbnailUrl, videoPosterUrl } from './types';

// Thumbnail / glyph for one file. Grid + list both use the SMALL thumbnail
// derivative (never the original, never the medium preview). On a 404 (thumbnail
// generation skipped/failed) or network error we fall back to a type glyph so
// the layout never collapses. Decoded lazily + async so a directory of many
// images doesn't block paint.

interface FileThumbProps {
  file: FileSummary;
  // 'grid' uses a larger square tile; 'list' uses a small leading icon.
  variant: 'grid' | 'list';
}

function glyphFor(mimeType: string): string {
  const mime = mimeType.toLowerCase();
  if (mime.startsWith('image/')) return '🖼️';
  if (mime.startsWith('video/')) return '🎬';
  if (mime.startsWith('audio/')) return '🎵';
  if (mime === 'application/pdf') return '📕';
  if (mime.startsWith('text/')) return '📄';
  if (mime.includes('zip') || mime.includes('compressed') || mime.includes('tar')) return '🗜️';
  return '📄';
}

export function FileThumb({ file, variant }: FileThumbProps) {
  const [failed, setFailed] = useState(false);
  const kind = mediaKindOf(file);
  const className = variant === 'grid' ? 'file-thumb file-thumb-grid' : 'file-thumb file-thumb-list';

  // Images get their small thumbnail; videos get their poster frame. Anything
  // else (or a failed media thumbnail) renders a glyph.
  const src = !failed
    ? isImage(file.mimeType)
      ? smallThumbnailUrl(file.id)
      : kind === 'video'
        ? videoPosterUrl(file.id)
        : null
    : null;

  if (src === null) {
    return (
      <span className={`${className} file-thumb-glyph`} aria-hidden="true">
        {glyphFor(file.mimeType)}
      </span>
    );
  }

  return (
    <img
      src={src}
      alt=""
      aria-hidden="true"
      className={`${className} file-thumb-img`}
      loading="lazy"
      decoding="async"
      draggable={false}
      onError={() => setFailed(true)}
    />
  );
}

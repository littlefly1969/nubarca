// Slide builders: turn screen-level items into viewer slides. OWNED media
// builds owner-scoped derivative paths (/api/files/{id}/...); SHARED media
// uses EXCLUSIVELY the server-provided album-scoped URLs carried on the item
// — never a hand-built /api/files path (that family is owner-only by design,
// and hand-building one for shared media would be a privacy hole).

import type { MediaItem, VideoMediaItem } from '../api/media';
import { filePreviewPath, fileVideoPath } from '../api/filePaths';
import { authenticatedSource } from './imageSource';
import type { ViewerSlide } from './viewerSequence';
import type { SharedAlbumItem } from '../api/sharedAlbums';

function isVideo(item: MediaItem): item is VideoMediaItem {
  return item.kind === 'video';
}

export function ownedSlides(items: MediaItem[]): ViewerSlide[] {
  return items.map((item) => {
    if (isVideo(item)) {
      const src = authenticatedSource(fileVideoPath(item.id));
      return {
        key: item.id,
        kind: 'video' as const,
        displayName: item.displayName,
        imagePath: '',
        videoSource: src ? { uri: src.uri, headers: src.headers } : null,
        posterUrl: item.posterUrl,
      };
    }
    return {
      key: item.id,
      kind: 'image' as const,
      displayName: item.displayName,
      imagePath: filePreviewPath(item.id),
      videoSource: null,
      posterUrl: null,
    };
  });
}

export function sharedSlides(items: SharedAlbumItem[]): ViewerSlide[] {
  return items.map((item) => {
    const src =
      item.kind === 'video' && item.videoUrl !== null
        ? authenticatedSource(item.videoUrl)
        : null;
    return {
      key: item.albumItemId,
      kind: item.kind,
      displayName: '', // shared items carry NO display name by contract
      imagePath: item.previewUrl,
      videoSource: src
        ? { uri: src.uri, headers: src.headers }
        : null,
      posterUrl: item.posterUrl,
    };
  });
}

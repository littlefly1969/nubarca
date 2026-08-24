// Authenticated native video source for expo-video.
//
// Playback reuses NubArca's existing Range-enabled owner endpoint
// GET /api/files/{id}/video. The source carries the exact session Cookie
// header — no temporary public URL is ever minted to bypass authentication.
// The playback contract lives HERE so an HLS/progressive selection can evolve
// later without touching any screen.

import { authenticatedSource, type AuthenticatedSource } from './imageSource.ts';
import { fileVideoPath } from '../api/filePaths.ts';
import type { VideoMediaItem } from '../api/media.ts';

export interface VideoPlaybackSource {
  // expo-video source payload.
  source: { uri: string; headers: { cookie: string } };
  metadata: {
    title: string;
    poster: string | null;
  };
}

export function videoFileVideoPath(fileId: string): string {
  return fileVideoPath(fileId);
}

// Build the expo-video source for one video item, or null when there is no
// session cookie (no authenticated source is ever constructed signed-out).
export function buildVideoSource(item: VideoMediaItem): VideoPlaybackSource | null {
  const src: AuthenticatedSource | null = authenticatedSource(
    videoFileVideoPath(item.id),
  );
  if (!src) return null;
  return {
    source: src,
    metadata: {
      title: item.displayName,
      poster:
        item.posterUrl !== null && item.posterUrl.length > 0
          ? item.posterUrl
          : null,
    },
  };
}

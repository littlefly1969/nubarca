// Videos: GET /api/videos. Mirrors frontend/packages/api-client/src/videos.ts
// (NubArca.Api.Files.VideoItem/VideoListResponse) for the subset mobile uses.
// posterUrl points at the existing GET /api/files/{id}/poster; playback uses
// GET /api/files/{id}/video (Range-enabled) via media/videoSource.

import { apiGet } from './client.ts';
import type { ImageSortDirection, ImageSortField } from './images';

export interface VideoItem {
  id: string;
  name: string;
  title: string | null;
  displayName: string;
  mimeType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  updatedAt: string | null;
  posterUrl: string;
  durationSeconds: number | null;
  occurrenceCount: number;
  // Poster provenance ("synthetic" | "ffmpeg" | ... | "null"); synthetic
  // placeholders are presented as such, never mistaken for a real frame.
  posterSource: string | null;
  previewStripUrl: string | null;
  hasDuplicates: boolean;
  videoCodec: string | null;
  audioCodec: string | null;
  hasAudio: boolean;
  frameRate: number | null;
}

export interface VideoListResponse {
  items: VideoItem[];
  limit: number;
  offset: number;
  count: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export interface ListVideosQuery {
  sort?: ImageSortField;
  direction?: ImageSortDirection;
  limit?: number;
  cursor?: string | null;
}

export function listVideos(
  query: ListVideosQuery = {},
  signal?: AbortSignal,
): Promise<VideoListResponse> {
  const params = new URLSearchParams();
  if (query.sort) params.set('sort', query.sort);
  if (query.direction) params.set('direction', query.direction);
  if (query.limit != null) params.set('limit', String(query.limit));
  if (query.cursor) params.set('cursor', query.cursor);
  const qs = params.toString();
  return apiGet<VideoListResponse>(`/api/videos${qs ? `?${qs}` : ''}`, signal);
}

// Small still for grid tiles (never the original).
export function smallThumbnailPath(fileId: string): string {
  return `/api/files/${fileId}/thumbnail?size=small`;
}

// Medium preview for the photo viewer (never the original full-res).
export function mediumPreviewPath(fileId: string): string {
  return `/api/files/${fileId}/preview`;
}

// Poster path for a video tile/player cover.
export function posterPath(fileId: string): string {
  return `/api/files/${fileId}/poster`;
}

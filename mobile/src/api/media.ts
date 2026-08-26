// Unified media surface: GET /api/media and GET /api/albums/{albumId}/media.
// Mirrors frontend/packages/api-client/src/media.ts (NubArca.Api.Files.MediaItem)
// for the subset mobile uses. One discriminated item type carries both images
// and videos so grids render a mixed, server-ordered sequence. No storage
// internals (no BlobObjectId, StorageKey, SHA, embeddings, raw metadata).

import { apiGet } from './client.ts';
import type { ImageSortDirection, ImageSortField } from './images';

export type MediaKind = 'image' | 'video';

interface MediaItemBase {
  id: string;
  kind: MediaKind;
  name: string;
  title: string | null;
  displayName: string;
  mimeType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  updatedAt: string | null;
  takenAt: string | null;
  thumbnailUrl: string; // small thumbnail (image) or poster (video)
  occurrenceCount: number;
  hasDuplicates: boolean;
}

export interface ImageMediaItem extends MediaItemBase {
  kind: 'image';
}

export interface VideoMediaItem extends MediaItemBase {
  kind: 'video';
  posterUrl: string | null;
  durationSeconds: number | null;
  videoCodec: string | null;
  audioCodec: string | null;
  hasAudio: boolean | null;
  frameRate: number | null;
  posterSource: string | null;
  previewStripUrl: string | null;
}

export type MediaItem = ImageMediaItem | VideoMediaItem;

export interface MediaListResponse {
  items: MediaItem[];
  limit: number;
  count: number;
  nextCursor: string | null;
  hasMore: boolean;
  total: number;
  photoCount: number;
  videoCount: number;
}

export interface ListMediaQuery {
  kind: 'all' | 'image' | 'video';
  sort?: ImageSortField;
  direction?: ImageSortDirection;
  limit?: number;
  cursor?: string | null;
}

function toParams(query: ListMediaQuery): URLSearchParams {
  const p = new URLSearchParams();
  p.set('kind', query.kind);
  if (query.sort) p.set('sort', query.sort);
  if (query.direction) p.set('direction', query.direction);
  if (query.limit !== undefined) p.set('limit', String(query.limit));
  if (query.cursor) p.set('cursor', query.cursor);
  return p;
}

// The whole-library mixed grid ("Tutti" experience) and the Photos/Videos tabs
// via kind filtering — one backend concept, one wire vocabulary.
export function listMedia(
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  const qs = toParams(query).toString();
  return apiGet<MediaListResponse>(`/api/media${qs ? `?${qs}` : ''}`, signal);
}

// Album detail's mixed-media grid. Same MediaItem projection as /api/media,
// scoped to one album's membership — no per-item metadata fan-out needed.
export function listAlbumMedia(
  albumId: string,
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  const qs = toParams(query).toString();
  return apiGet<MediaListResponse>(
    `/api/albums/${albumId}/media${qs ? `?${qs}` : ''}`,
    signal,
  );
}

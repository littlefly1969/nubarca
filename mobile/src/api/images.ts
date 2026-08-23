// Images: GET /api/images. Mirrors frontend/packages/api-client/src/images.ts
// (NubArca.Api.Files.ImageItem/ImageListResponse) for the subset mobile uses.
// thumbnailUrl is the existing small-thumbnail derivative; the URL may 404
// for images whose thumbnail was skipped — the tile shows a placeholder then.

import { apiGet } from './client.ts';

export interface ImageItem {
  id: string;
  name: string;
  title: string | null;
  // What the UI renders: title when present, else name.
  displayName: string;
  mimeType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  updatedAt: string | null;
  thumbnailUrl: string;
  // 1 when no duplicates; >1 means this blob appears N times in the library.
  // Never exposes SHA-256, BlobObjectId, or internal ids.
  occurrenceCount: number;
  hasDuplicates: boolean;
}

export interface ImageListResponse {
  items: ImageItem[];
  limit: number;
  offset: number;
  count: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export type ImageSortField = 'created' | 'name' | 'size' | 'datetaken';
export type ImageSortDirection = 'asc' | 'desc';

export interface ListImagesQuery {
  sort?: ImageSortField;
  direction?: ImageSortDirection;
  limit?: number;
  cursor?: string | null;
}

export function listImages(
  query: ListImagesQuery = {},
  signal?: AbortSignal,
): Promise<ImageListResponse> {
  const params = new URLSearchParams();
  if (query.sort) params.set('sort', query.sort);
  if (query.direction) params.set('direction', query.direction);
  if (query.limit != null) params.set('limit', String(query.limit));
  if (query.cursor) params.set('cursor', query.cursor);
  const qs = params.toString();
  return apiGet<ImageListResponse>(`/api/images${qs ? `?${qs}` : ''}`, signal);
}

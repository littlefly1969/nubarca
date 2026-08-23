// Albums: owner album CRUD, membership and bulk membership. Mirrors
// frontend/packages/api-client/src/albums.ts exactly — same routes, same DTO
// shapes, same safe counts-only bulk result.
//
// THE membership rule this module encodes: album item removal is ALBUM
// MEMBERSHIP ONLY. DELETE on /api/albums/{id}/items(/bulk) never deletes the
// underlying FileItem or blob; DELETE /api/albums/{id} deletes the album, not
// its media. The album contract test pins these method/path pairs.

import { apiDelete, apiGet, apiPatch, apiPost } from './client.ts';

// One tile of an album card's cover mosaic. thumbnailUrl is the small
// thumbnail for images and the poster for videos.
export interface AlbumCoverItem {
  fileItemId: string;
  kind: 'image' | 'video';
  thumbnailUrl: string;
}

export interface AlbumSummary {
  id: string;
  name: string;
  description: string | null;
  itemCount: number;
  showOnTv: boolean;
  createdAt: string;
  updatedAt: string;
  photoCount: number;
  videoCount: number;
  excludedCount: number;
  coverItems: AlbumCoverItem[];
}

export interface AlbumDetail {
  id: string;
  name: string;
  description: string | null;
  showOnTv: boolean;
  createdAt: string;
  updatedAt: string;
}

// Safe counts-only summary of a bulk add/remove. Never reveals which specific
// ids were foreign/missing (that would leak another owner's file existence).
export interface BulkAlbumItemsResult {
  requested: number;
  succeeded: number;
  skipped: number;
}

export function listAlbums(signal?: AbortSignal): Promise<AlbumSummary[]> {
  return apiGet<AlbumSummary[]>('/api/albums', signal);
}

export function createAlbum(
  name: string,
  description: string | null = null,
  signal?: AbortSignal,
): Promise<AlbumDetail> {
  return apiPost<AlbumDetail>('/api/albums', { name, description }, { signal });
}

export function getAlbum(albumId: string, signal?: AbortSignal): Promise<AlbumDetail> {
  return apiGet<AlbumDetail>(`/api/albums/${albumId}`, signal);
}

export function updateAlbum(
  albumId: string,
  name: string,
  description: string | null = null,
  signal?: AbortSignal,
): Promise<AlbumDetail> {
  return apiPatch<AlbumDetail>(
    `/api/albums/${albumId}`,
    { name, description },
    { signal },
  );
}

// Deletes the ALBUM. The underlying media is untouched.
export function deleteAlbum(albumId: string, signal?: AbortSignal): Promise<void> {
  return apiDelete<void>(`/api/albums/${albumId}`, undefined, { signal });
}

// Bulk ADD many gallery-selected files to an album. Idempotent: files already
// present (or duplicated in the request) count as skipped, not errors.
export function bulkAddAlbumItems(
  albumId: string,
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<BulkAlbumItemsResult> {
  return apiPost<BulkAlbumItemsResult>(
    `/api/albums/${albumId}/items/bulk`,
    { fileItemIds },
    { signal },
  );
}

// Bulk REMOVE many files from an album. Album membership only — the
// underlying FileItem/blob is never deleted.
export function bulkRemoveAlbumItems(
  albumId: string,
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<BulkAlbumItemsResult> {
  return apiDelete<BulkAlbumItemsResult>(
    `/api/albums/${albumId}/items/bulk`,
    { fileItemIds },
    { signal },
  );
}

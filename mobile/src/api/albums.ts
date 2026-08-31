// Mobile TRANSPORT for owner album CRUD, membership and bulk membership.
//
// The DTOs, the routes and the mutation payloads come from
// @nubarca/contracts — one definition for web, phone and television. What
// stays here is the authenticated mobile transport.
//
// THE membership rule this surface encodes: album item removal is ALBUM
// MEMBERSHIP ONLY. DELETE on /api/albums/{id}/items(/bulk) never deletes the
// underlying FileItem or blob; DELETE /api/albums/{id} deletes the album, not
// its media. The album contract test pins these method/path pairs.

import { apiDelete, apiGet, apiPatch, apiPost } from './client.ts';
import type {
  AlbumDetail,
  AlbumSummary,
  BulkAlbumItemsResult,
} from '@nubarca/contracts';
import { albumTvSettingsPath } from '@nubarca/contracts';

export type {
  AlbumCoverItem,
  AlbumDetail,
  AlbumItemSummary,
  AlbumSummary,
  BulkAlbumItemsResult,
} from '@nubarca/contracts';

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

// TV visibility has its own route, so a rename cannot flip it by accident.
export function setAlbumTvVisibility(
  albumId: string,
  showOnTv: boolean,
  signal?: AbortSignal,
): Promise<AlbumDetail> {
  return apiPatch<AlbumDetail>(albumTvSettingsPath(albumId), { showOnTv }, { signal });
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

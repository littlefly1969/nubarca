import type {
  AlbumDetail,
  AlbumItemSummary,
  AlbumSummary,
  BulkAlbumItemsResult,
} from '@nubarca/contracts';
import { api } from './client';

// Web TRANSPORT for owner album CRUD and membership.
//
// The DTOs come from @nubarca/contracts — one definition for web, phone and
// television — and are re-exported here under their existing names so every
// web call site keeps importing from '@nubarca/api-client' unchanged.
//
// THE membership rule (§24): removing an item from an album changes ALBUM
// MEMBERSHIP only; it never deletes the FileItem or its blob. Deleting an
// album deletes the album, not its media.

export type {
  AlbumCoverItem,
  AlbumDetail,
  AlbumItemSummary,
  AlbumSummary,
  BulkAlbumItemsResult,
} from '@nubarca/contracts';

export async function listAlbums(signal?: AbortSignal): Promise<AlbumSummary[]> {
  return api<AlbumSummary[]>('/api/albums', { signal });
}

export async function createAlbum(
  name: string,
  description?: string | null,
  signal?: AbortSignal,
): Promise<AlbumDetail> {
  return api<AlbumDetail>('/api/albums', {
    method: 'POST',
    json: { name, description: description ?? null },
    signal,
  });
}

export async function getAlbum(albumId: string, signal?: AbortSignal): Promise<AlbumDetail> {
  return api<AlbumDetail>(`/api/albums/${albumId}`, { signal });
}

export async function updateAlbum(
  albumId: string,
  name: string,
  description?: string | null,
  signal?: AbortSignal,
): Promise<AlbumDetail> {
  return api<AlbumDetail>(`/api/albums/${albumId}`, {
    method: 'PATCH',
    json: { name, description: description ?? null },
    signal,
  });
}

export async function setAlbumTvVisibility(
  albumId: string,
  showOnTv: boolean,
  signal?: AbortSignal,
): Promise<AlbumDetail> {
  return api<AlbumDetail>(`/api/albums/${albumId}/tv-settings`, {
    method: 'PATCH',
    json: { showOnTv },
    signal,
  });
}

export async function deleteAlbum(albumId: string, signal?: AbortSignal): Promise<void> {
  await api<void>(`/api/albums/${albumId}`, { method: 'DELETE', signal });
}

export async function listAlbumItems(albumId: string, signal?: AbortSignal): Promise<AlbumItemSummary[]> {
  return api<AlbumItemSummary[]>(`/api/albums/${albumId}/items`, { signal });
}

export async function addAlbumItem(
  albumId: string,
  fileItemId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/albums/${albumId}/items`, {
    method: 'POST',
    json: { fileItemId },
    signal,
  });
}

export async function removeAlbumItem(
  albumId: string,
  fileItemId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/albums/${albumId}/items/${fileItemId}`, {
    method: 'DELETE',
    signal,
  });
}


// Bulk add many gallery-selected files to an album. Idempotent: files already
// present (or duplicated in the request) count as skipped, not errors.
export async function bulkAddAlbumItems(
  albumId: string,
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<BulkAlbumItemsResult> {
  return api<BulkAlbumItemsResult>(`/api/albums/${albumId}/items/bulk`, {
    method: 'POST',
    json: { fileItemIds },
    signal,
  });
}

// Bulk remove many files from an album. Album membership only — the underlying
// FileItem/blob is never deleted.
export async function bulkRemoveAlbumItems(
  albumId: string,
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<BulkAlbumItemsResult> {
  return api<BulkAlbumItemsResult>(`/api/albums/${albumId}/items/bulk`, {
    method: 'DELETE',
    json: { fileItemIds },
    signal,
  });
}

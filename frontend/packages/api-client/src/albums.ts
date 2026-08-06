import { api } from './client';

// One tile of an album card's cover mosaic. Mirrors
// NubArca.Api.Albums.AlbumCoverItem. `thumbnailUrl` is the small thumbnail for
// images and the poster for videos.
export interface AlbumCoverItem {
  fileItemId: string;
  kind: 'image' | 'video';
  thumbnailUrl: string;
}

export interface AlbumSummary {
  id: string;
  name: string;
  description: string | null;
  // Raw membership count (backward compatible).
  itemCount: number;
  showOnTv: boolean;
  createdAt: string;
  updatedAt: string;
  // Slice 5: per-kind active counts + excluded members + cover mosaic (≤4).
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

export interface AlbumItemSummary {
  fileItemId: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  addedAt: string;
  thumbnailUrl: string | null;
}

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

// Safe counts-only summary of a bulk add/remove. Never reveals which specific
// ids were foreign/missing (that would leak existence of another owner's files).
export interface BulkAlbumItemsResult {
  requested: number;
  succeeded: number;
  skipped: number;
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

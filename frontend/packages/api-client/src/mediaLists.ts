// Owner media lists and the account's current selection.
//
// A list holds references to the owner's own media and nothing else: never a
// copy, a URL or a token. Everything here is owner-private; a foreign or
// missing list is a 404.

import type { MediaItem } from '@nubarca/contracts';
import { api } from './client';

export interface MediaListSummary {
  id: string;
  // Null for the current selection.
  name: string | null;
  kind: 'named' | 'selection';
  // Moves on every change to the name or the contents.
  version: number;
  // Media the owner can still see (trashed and Vault media are not counted).
  count: number;
  createdAt: string;
  updatedAt: string;
  // A digest of what the list shows now; it moves when a listed file is
  // trashed, vaulted or restored, which `version` does not. Null in overviews.
  stamp?: string | null;
}

export interface MediaListItemsResult {
  requested: number;
  succeeded: number;
  skipped: number;
  version: number;
  count: number;
  stamp: string;
}

export interface MediaListIdsPage {
  ids: string[];
  nextCursor: string | null;
  version: number;
  count: number;
}

export interface MediaListMediaPage {
  items: MediaItem[];
  nextCursor: string | null;
  version: number;
  count: number;
}

/** At most this many ids per add/remove request (the server refuses more). */
export const MEDIA_LIST_MAX_ITEMS_PER_REQUEST = 3000;

export function getCurrentSelection(signal?: AbortSignal): Promise<MediaListSummary> {
  return api<MediaListSummary>('/api/selection', { signal });
}

export function listMediaLists(signal?: AbortSignal): Promise<MediaListSummary[]> {
  return api<MediaListSummary[]>('/api/lists', { signal });
}

export function getMediaList(listId: string, signal?: AbortSignal): Promise<MediaListSummary> {
  return api<MediaListSummary>(`/api/lists/${listId}`, { signal });
}

export function createMediaList(
  name: string, fileItemIds: string[] = [], signal?: AbortSignal,
): Promise<MediaListSummary> {
  return api<MediaListSummary>('/api/lists', { method: 'POST', json: { name, fileItemIds }, signal });
}

export function renameMediaList(
  listId: string, name: string, expectedVersion?: number, signal?: AbortSignal,
): Promise<MediaListSummary> {
  return api<MediaListSummary>(`/api/lists/${listId}`, {
    method: 'PATCH', json: { name, expectedVersion }, signal,
  });
}

// Also how the current selection is saved as a named list.
export function duplicateMediaList(
  listId: string, name?: string, signal?: AbortSignal,
): Promise<MediaListSummary> {
  return api<MediaListSummary>(`/api/lists/${listId}/duplicate`, { method: 'POST', json: { name }, signal });
}

export async function deleteMediaList(listId: string, signal?: AbortSignal): Promise<void> {
  await api<void>(`/api/lists/${listId}`, { method: 'DELETE', signal });
}

export function addMediaListItems(
  listId: string, fileItemIds: string[], signal?: AbortSignal,
): Promise<MediaListItemsResult> {
  return api<MediaListItemsResult>(`/api/lists/${listId}/items`, { method: 'POST', json: { fileItemIds }, signal });
}

export function removeMediaListItems(
  listId: string, fileItemIds: string[], signal?: AbortSignal,
): Promise<MediaListItemsResult> {
  return api<MediaListItemsResult>(`/api/lists/${listId}/items`, { method: 'DELETE', json: { fileItemIds }, signal });
}

export function clearMediaList(listId: string, signal?: AbortSignal): Promise<MediaListItemsResult> {
  return api<MediaListItemsResult>(`/api/lists/${listId}/clear`, { method: 'POST', signal });
}

export function listMediaListIds(
  listId: string, cursor?: string | null, limit = 500, signal?: AbortSignal,
): Promise<MediaListIdsPage> {
  const params = new URLSearchParams({ limit: String(limit) });
  if (cursor) params.set('cursor', cursor);
  return api<MediaListIdsPage>(`/api/lists/${listId}/ids?${params.toString()}`, { signal });
}

export function listMediaListItems(
  listId: string, cursor?: string | null, limit = 60, signal?: AbortSignal,
): Promise<MediaListMediaPage> {
  const params = new URLSearchParams({ limit: String(limit) });
  if (cursor) params.set('cursor', cursor);
  return api<MediaListMediaPage>(`/api/lists/${listId}/items?${params.toString()}`, { signal });
}

// ── A member's own selection inside one shared album ───────────────────────
//
// Personal and scoped to that album: never the account selection, never a list.
// A revoked or missing membership is a 404 on every call.

export interface SharedAlbumSelectionState { version: number; count: number }

export function getSharedAlbumSelection(albumId: string, signal?: AbortSignal): Promise<SharedAlbumSelectionState> {
  return api<SharedAlbumSelectionState>(`/api/shared-albums/${albumId}/selection`, { signal });
}

export function listSharedAlbumSelectionIds(
  albumId: string, cursor?: string | null, limit = 500, signal?: AbortSignal,
): Promise<MediaListIdsPage> {
  const params = new URLSearchParams({ limit: String(limit) });
  if (cursor) params.set('cursor', cursor);
  return api<MediaListIdsPage>(`/api/shared-albums/${albumId}/selection/ids?${params.toString()}`, { signal });
}

export function addSharedAlbumSelectionItems(
  albumId: string, fileItemIds: string[], signal?: AbortSignal,
): Promise<MediaListItemsResult> {
  return api<MediaListItemsResult>(`/api/shared-albums/${albumId}/selection/items`, { method: 'POST', json: { fileItemIds }, signal });
}

export function removeSharedAlbumSelectionItems(
  albumId: string, fileItemIds: string[], signal?: AbortSignal,
): Promise<MediaListItemsResult> {
  return api<MediaListItemsResult>(`/api/shared-albums/${albumId}/selection/items`, { method: 'DELETE', json: { fileItemIds }, signal });
}

export function clearSharedAlbumSelection(albumId: string, signal?: AbortSignal): Promise<MediaListItemsResult> {
  return api<MediaListItemsResult>(`/api/shared-albums/${albumId}/selection/clear`, { method: 'POST', signal });
}

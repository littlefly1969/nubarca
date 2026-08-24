// Shared albums: the RECIPIENT's view of albums shared with them, plus their
// pending invitations. Mobile mirror of frontend/packages/api-client/src/
// albumSharing.ts (SHARE-ALBUM-01/02 subset for mobile v1).
//
// THE authority rule this module encodes: every media URL below arrives
// READY-BUILT from the server and is album-scoped
// (/api/shared-albums/{albumId}/media/{fileItemId}/...). They are re-
// authorized on every request against the caller's grant. A client must never
// construct /api/files/{fileId}/... for shared media — that family is owner-
// only by design, and hand-building one would be a privacy hole, not a
// convenience.

import { apiDelete, apiGet, apiPost } from './client.ts';
import type { BulkAlbumItemsResult } from './albums.ts';

export type AlbumRole = 'viewer' | 'contributor' | 'editor';

export interface SharedAlbumCoverItem {
  fileItemId: string;
  kind: 'image' | 'video';
  thumbnailUrl: string;
}

export interface SharedAlbumSummary {
  albumId: string;
  name: string;
  description: string | null;
  ownerDisplayName: string;
  role: AlbumRole;
  allowOriginalDownload: boolean;
  itemCount: number;
  sharedAt: string;
  coverItems: SharedAlbumCoverItem[];
}

export interface SharedAlbumDetail {
  albumId: string;
  name: string;
  description: string | null;
  ownerDisplayName: string;
  role: AlbumRole;
  allowOriginalDownload: boolean;
  itemCount: number;
  version: number;
  canEdit: boolean;
}

// One media item of a shared album. NO display name by contract (owner free
// text can carry personal data the recipient does not need). Every *Url is
// server-provided and album-scoped; downloadUrl is null unless the membership
// permits originals. canWithdraw marks the caller's OWN contribution.
export interface SharedAlbumItem {
  albumItemId: string;
  fileItemId: string;
  kind: 'image' | 'video';
  thumbnailUrl: string;
  previewUrl: string;
  posterUrl: string | null;
  videoUrl: string | null;
  downloadUrl: string | null;
  width: number | null;
  height: number | null;
  addedAt: string;
  canWithdraw: boolean;
}

export interface AlbumInvitation {
  membershipId: string;
  albumId: string;
  albumName: string;
  albumDescription: string | null;
  ownerDisplayName: string;
  role: AlbumRole;
  allowOriginalDownload: boolean;
  itemCount: number;
  invitedAt: string;
}

export type SharedAlbumItemKind = 'all' | 'image' | 'video';

export interface SharedAlbumItemsPage {
  items: SharedAlbumItem[];
  // Null means "that was the last page", never "ask again".
  nextCursor: string | null;
  total: number;
  photoCount: number;
  videoCount: number;
}

export interface SharedAlbumItemsQuery {
  kind?: SharedAlbumItemKind;
  cursor?: string | null;
  limit?: number;
}

export function listSharedAlbums(signal?: AbortSignal): Promise<SharedAlbumSummary[]> {
  return apiGet<SharedAlbumSummary[]>('/api/shared-albums', signal);
}

export function getSharedAlbum(albumId: string, signal?: AbortSignal): Promise<SharedAlbumDetail> {
  return apiGet<SharedAlbumDetail>(`/api/shared-albums/${albumId}`, signal);
}

export function listSharedAlbumItems(
  albumId: string,
  query: SharedAlbumItemsQuery = {},
  signal?: AbortSignal,
): Promise<SharedAlbumItemsPage> {
  const params = new URLSearchParams();
  if (query.kind && query.kind !== 'all') params.set('kind', query.kind);
  if (query.cursor) params.set('cursor', query.cursor);
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  const qs = params.toString();
  return apiGet<SharedAlbumItemsPage>(
    `/api/shared-albums/${albumId}/items${qs ? `?${qs}` : ''}`,
    signal,
  );
}

export function listAlbumInvitations(signal?: AbortSignal): Promise<AlbumInvitation[]> {
  return apiGet<AlbumInvitation[]>('/api/shared-albums/invitations', signal);
}

export function acceptAlbumInvitation(membershipId: string, signal?: AbortSignal): Promise<void> {
  return apiPost<void>(`/api/shared-albums/invitations/${membershipId}/accept`, undefined, {
    signal,
  });
}

export function declineAlbumInvitation(membershipId: string, signal?: AbortSignal): Promise<void> {
  return apiPost<void>(`/api/shared-albums/invitations/${membershipId}/decline`, undefined, {
    signal,
  });
}

// Link media the CALLER owns into somebody else's album. No copy, no ownership
// transfer — counts-only result, never which ids were skipped.
export function bulkContributeToSharedAlbum(
  albumId: string,
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<BulkAlbumItemsResult> {
  return apiPost<BulkAlbumItemsResult>(
    `/api/shared-albums/${albumId}/contributions/bulk`,
    { fileItemIds },
    { signal },
  );
}

// Take your own contribution back out. Never deletes the file; still permitted
// after a downgrade to Viewer — the per-item canWithdraw is the gate.
export function withdrawSharedAlbumContribution(
  albumId: string,
  fileItemId: string,
  signal?: AbortSignal,
): Promise<void> {
  return apiDelete<void>(
    `/api/shared-albums/${albumId}/contributions/${fileItemId}`,
    undefined,
    { signal },
  );
}

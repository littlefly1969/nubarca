import { api, ApiError } from './client';
import type {
  AlbumContentResponse,
  AlbumInvitation,
  AlbumMember,
  AlbumRole,
  BulkAlbumItemsResult,
  ResolvedAlbumRecipient,
  SharedAlbumDetail,
  SharedAlbumItemsPage,
  SharedAlbumItemsQuery,
  SharedAlbumSummary,
} from '@nubarca/contracts';

// Web TRANSPORT for album sharing. The DTOs, roles, membership states, routes
// and payloads are canonical in @nubarca/contracts, shared with the phone, and
// re-exported here under their existing names so every web call site is
// unchanged.

export type {
  AlbumContentItem,
  AlbumContentResponse,
  AlbumInvitation,
  AlbumMember,
  AlbumMembershipState,
  AlbumRole,
  ResolvedAlbumRecipient,
  SharedAlbumCapabilities,
  SharedAlbumCoverItem,
  SharedAlbumDetail,
  SharedAlbumItem,
  SharedAlbumItemKind,
  SharedAlbumItemsPage,
  SharedAlbumItemsQuery,
  SharedAlbumSummary,
} from '@nubarca/contracts';
import {
  ALBUM_INVITATIONS_PATH,
  albumInvitationPath,
  sharedAlbumItemsPath,
  sharedAlbumItemsQueryToParams,
  withQuery,
} from '@nubarca/contracts';
export {
  isActiveMembership,
  isHistoricalMembership,
  sharedAlbumCapabilities,
} from '@nubarca/contracts';

// ── Owner side ──────────────────────────────────────────────────────────────

export async function listAlbumMembers(
  albumId: string,
  signal?: AbortSignal,
): Promise<AlbumMember[]> {
  return api<AlbumMember[]>(`/api/albums/${albumId}/members`, { signal });
}

// Confirms an exact email belongs to an invitable account, returning only the
// display name so the owner can check they have the right person before
// sending. POST, not GET: the address must never land in a URL, a server access
// log, browser history, or a Referer header. A 404 means "cannot be invited"
// and covers unknown / disabled / self indistinguishably.
export async function resolveAlbumRecipient(
  albumId: string,
  email: string,
  signal?: AbortSignal,
): Promise<ResolvedAlbumRecipient> {
  return api<ResolvedAlbumRecipient>(`/api/albums/${albumId}/members/resolve`, {
    method: 'POST',
    json: { email },
    signal,
  });
}

export async function inviteAlbumMember(
  albumId: string,
  email: string,
  options?: { role?: AlbumRole; allowOriginalDownload?: boolean },
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return api<AlbumMember>(`/api/albums/${albumId}/members`, {
    method: 'POST',
    json: {
      email,
      role: options?.role ?? 'viewer',
      allowOriginalDownload: options?.allowOriginalDownload ?? false,
    },
    signal,
  });
}

export async function setAlbumMemberDownload(
  albumId: string,
  membershipId: string,
  allowOriginalDownload: boolean,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return api<AlbumMember>(`/api/albums/${albumId}/members/${membershipId}`, {
    method: 'PATCH',
    json: { allowOriginalDownload },
    signal,
  });
}

// Grants or revokes the narrow party-message delegation. Owner-only, and
// deliberately NOT a role change: the member keeps whatever viewer/contributor/
// editor role they have, and gains only the ability to approve, hide, restore
// and Hero-promote this album's guest messages. Revoking takes effect on their
// very next request.
//
// `allowOriginalDownload` is resent because the endpoint takes it as a required
// field; passing the member's current value keeps this call from changing it as
// a side effect.
export async function setAlbumMemberPartyMessages(
  albumId: string,
  membershipId: string,
  canManagePartyMessages: boolean,
  allowOriginalDownload: boolean,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return api<AlbumMember>(`/api/albums/${albumId}/members/${membershipId}`, {
    method: 'PATCH',
    json: { allowOriginalDownload, canManagePartyMessages },
    signal,
  });
}

// Cancels a pending invitation OR revokes an accepted membership — one call,
// because both mean the same thing to the person on the other end. Takes effect
// on that person's very next request; nothing is cached.
export async function revokeAlbumMember(
  albumId: string,
  membershipId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/albums/${albumId}/members/${membershipId}`, {
    method: 'DELETE',
    signal,
  });
}

// ── Recipient side ──────────────────────────────────────────────────────────

export async function listSharedAlbums(signal?: AbortSignal): Promise<SharedAlbumSummary[]> {
  return api<SharedAlbumSummary[]>('/api/shared-albums', { signal });
}

export async function getSharedAlbum(
  albumId: string,
  signal?: AbortSignal,
): Promise<SharedAlbumDetail> {
  return api<SharedAlbumDetail>(`/api/shared-albums/${albumId}`, { signal });
}

// One page of a shared album in its curated order. The cursor is opaque and is
// bound server-side to the kind it was issued for: pass back exactly what the
// previous page returned, never a hand-built one.
export async function listSharedAlbumItems(
  albumId: string,
  query: SharedAlbumItemsQuery = {},
  signal?: AbortSignal,
): Promise<SharedAlbumItemsPage> {
  return api<SharedAlbumItemsPage>(
    withQuery(sharedAlbumItemsPath(albumId), sharedAlbumItemsQueryToParams(query)),
    { signal },
  );
}

export async function listAlbumInvitations(signal?: AbortSignal): Promise<AlbumInvitation[]> {
  return api<AlbumInvitation[]>(ALBUM_INVITATIONS_PATH, { signal });
}

export async function acceptAlbumInvitation(
  membershipId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(albumInvitationPath(membershipId, 'accept'), {
    method: 'POST',
    signal,
  });
}

export async function declineAlbumInvitation(
  membershipId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(albumInvitationPath(membershipId, 'decline'), {
    method: 'POST',
    signal,
  });
}

// ── SHARE-ALBUM-02: roles, contributions, owner moderation ──────────────────

// Promote Viewer → Contributor or demote Contributor → Viewer. Owner-only.
// `editor` exists in the backend catalog for a later slice and is refused here
// and server-side; AlbumRole's assignable union deliberately excludes it.
// SHARE-ALBUM-03: all three catalog roles are assignable. "owner" is not in
// AlbumRole at all, so granting ownership is unrepresentable rather than merely
// rejected.
export type AssignableAlbumRole = AlbumRole;

export const ASSIGNABLE_ALBUM_ROLES: readonly AssignableAlbumRole[] =
  ['viewer', 'contributor', 'editor'] as const;

export async function setAlbumMemberRole(
  albumId: string,
  membershipId: string,
  role: AssignableAlbumRole,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return api<AlbumMember>(`/api/albums/${albumId}/members/${membershipId}/role`, {
    method: 'PATCH',
    json: { role },
    signal,
  });
}

// The owner's moderation view of the live album: their own media plus every
// linked contribution, with provenance and current source state.
export async function listAlbumContent(
  albumId: string,
  signal?: AbortSignal,
): Promise<AlbumContentResponse> {
  return api<AlbumContentResponse>(`/api/albums/${albumId}/content`, { signal });
}

// The owner removing ANY item from their album — their own or a contribution.
// Album membership only: the source file is never deleted, and for a
// collaborator's media the owner could not delete it even if they tried.
export async function removeAlbumContentItem(
  albumId: string,
  fileItemId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/albums/${albumId}/content/${fileItemId}`, {
    method: 'DELETE',
    signal,
  });
}

// Link media the CALLER owns into a shared album. No copy is made and ownership
// does not move — the file stays in the contributor's library.
export async function contributeToSharedAlbum(
  albumId: string,
  fileItemId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/shared-albums/${albumId}/contributions`, {
    method: 'POST',
    json: { fileItemId },
    signal,
  });
}

// The same link for a WHOLE selection, which is how media is chosen now: in the
// caller's own Media Library, then sent as a set. Reuses the album bulk result
// shape because it means exactly the same thing here — counts only, never which
// ids were skipped. Duplicates, items already in the album and anything
// ineligible are skipped rather than failing the request.
export async function bulkContributeToSharedAlbum(
  albumId: string,
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<BulkAlbumItemsResult> {
  return api<BulkAlbumItemsResult>(`/api/shared-albums/${albumId}/contributions/bulk`, {
    method: 'POST',
    json: { fileItemIds },
    signal,
  });
}

// Take your own contribution back out of the album. Never deletes the file, and
// still permitted after a downgrade to Viewer.
export async function withdrawSharedAlbumContribution(
  albumId: string,
  fileItemId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/shared-albums/${albumId}/contributions/${fileItemId}`, {
    method: 'DELETE',
    signal,
  });
}

// ── SHARE-ALBUM-03: collaborative editing ───────────────────────────────────
//
// Owner and Editor use the SAME endpoints and the same concurrency model. Every
// mutation echoes the `version` last read; a 409 means somebody else changed the
// album first and carries the CURRENT state so the client can refresh and
// explain, rather than retry.

export interface AlbumEditResult {
  albumId: string;
  version: number;
  name: string;
  description: string | null;
  coverFileItemId: string | null;
}

// The body of a 409. `ApiError.body` is typed as unknown, so call sites narrow
// through this shape rather than casting at each one.
export interface AlbumEditConflict extends AlbumEditResult {
  error: string;
}

export function isAlbumEditConflict(err: unknown): err is ApiError {
  return err instanceof ApiError && err.status === 409;
}

export async function editSharedAlbumDetails(
  albumId: string,
  expectedVersion: number,
  changes: { name?: string; description?: string },
  signal?: AbortSignal,
): Promise<AlbumEditResult> {
  return api<AlbumEditResult>(`/api/shared-albums/${albumId}`, {
    method: 'PATCH',
    json: { expectedVersion, ...changes },
    signal,
  });
}

// `fileItemId: null` clears the chosen cover and returns the album to the
// derived one. The server refuses any item that is not a currently-servable
// member, so this can never become a way to name arbitrary media.
export async function setSharedAlbumCover(
  albumId: string,
  expectedVersion: number,
  fileItemId: string | null,
  signal?: AbortSignal,
): Promise<AlbumEditResult> {
  return api<AlbumEditResult>(`/api/shared-albums/${albumId}/cover`, {
    method: 'PUT',
    json: { expectedVersion, fileItemId },
    signal,
  });
}

// The COMPLETE ordered list of AlbumItem ids — the server rejects a partial or
// duplicated one rather than interpreting it.
export async function reorderSharedAlbum(
  albumId: string,
  expectedVersion: number,
  albumItemIds: string[],
  signal?: AbortSignal,
): Promise<AlbumEditResult> {
  return api<AlbumEditResult>(`/api/shared-albums/${albumId}/order`, {
    method: 'PUT',
    json: { expectedVersion, albumItemIds },
    signal,
  });
}

// Editorial removal of ANY item. Removes the album membership only — the source
// file is never deleted, and for another user's media it could not be.
export async function removeSharedAlbumItem(
  albumId: string,
  albumItemId: string,
  expectedVersion: number,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(
    `/api/shared-albums/${albumId}/items/${albumItemId}?expectedVersion=${expectedVersion}`,
    { method: 'DELETE', signal },
  );
}

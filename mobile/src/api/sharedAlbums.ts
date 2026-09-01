// Mobile TRANSPORT for album sharing — both sides of it.
//
// The DTOs, roles, membership states, routes and payloads come from
// @nubarca/contracts: one definition for web, phone and television. What stays
// here is the authenticated mobile transport.
//
// PRIVACY (§26) is carried by the shared types rather than by care taken here:
// there is no email field on a member (only a masked address, owner-only), no
// user id anywhere, and a membership is addressed by membershipId. Media URLs
// for a recipient arrive ready-built and album-scoped (§28) — this module
// never constructs one from a file id, and never rewrites a shared URL into an
// owner route.

import { apiDelete, apiGet, apiPatch, apiPost } from './client.ts';
import type {
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
import {
  ALBUM_INVITATIONS_PATH,
  albumInvitationPath,
  albumMemberDownloadPath,
  albumMemberPartyMessagesPath,
  albumMemberPath,
  albumMemberRolePath,
  albumMembersPath,
  albumRecipientResolvePath,
  sharedAlbumItemsPath,
  sharedAlbumItemsQueryToParams,
  withQuery,
} from '@nubarca/contracts';

export type {
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
export { sharedAlbumCapabilities, isActiveMembership, isHistoricalMembership } from '@nubarca/contracts';

// ── Owner side (§25) ───────────────────────────────────────────────────────

/** Every membership row, live and historical. The owner sees both. */
export function listAlbumMembers(albumId: string, signal?: AbortSignal): Promise<AlbumMember[]> {
  return apiGet<AlbumMember[]>(albumMembersPath(albumId), signal);
}

/**
 * Resolve an EXACT address to a display name, for confirmation before
 * inviting. There is no directory and no autocomplete: a lookup that accepted
 * prefixes would let anyone enumerate accounts. The server answers the same
 * way for "no such account" as for anything else it will not disclose, and the
 * caller must not try to tell those apart.
 */
export function resolveAlbumRecipient(
  albumId: string,
  email: string,
  signal?: AbortSignal,
): Promise<ResolvedAlbumRecipient> {
  return apiPost<ResolvedAlbumRecipient>(albumRecipientResolvePath(albumId), { email }, { signal });
}

export function inviteAlbumMember(
  albumId: string,
  email: string,
  role: AlbumRole,
  allowOriginalDownload: boolean,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return apiPost<AlbumMember>(
    albumMembersPath(albumId),
    { email, role, allowOriginalDownload },
    { signal },
  );
}

export function setAlbumMemberRole(
  albumId: string,
  membershipId: string,
  role: AlbumRole,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return apiPatch<AlbumMember>(albumMemberRolePath(albumId, membershipId), { role }, { signal });
}

export function setAlbumMemberDownload(
  albumId: string,
  membershipId: string,
  allowOriginalDownload: boolean,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return apiPatch<AlbumMember>(
    albumMemberDownloadPath(albumId, membershipId),
    { allowOriginalDownload },
    { signal },
  );
}

/**
 * Grant or revoke the narrow party-MESSAGE moderation delegation (§37).
 * Not a role, not party administration, not general moderation — and only
 * meaningful while the membership is accepted and not revoked.
 */
export function setAlbumMemberPartyMessages(
  albumId: string,
  membershipId: string,
  canManagePartyMessages: boolean,
  signal?: AbortSignal,
): Promise<AlbumMember> {
  return apiPatch<AlbumMember>(
    albumMemberPartyMessagesPath(albumId, membershipId),
    { canManagePartyMessages },
    { signal },
  );
}

/** Revoke a pending invitation or an accepted membership. Same route. */
export function revokeAlbumMember(
  albumId: string,
  membershipId: string,
  signal?: AbortSignal,
): Promise<void> {
  return apiDelete<void>(albumMemberPath(albumId, membershipId), undefined, { signal });
}

// ── Recipient side (§27) ───────────────────────────────────────────────────

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
  return apiGet<SharedAlbumItemsPage>(
    withQuery(sharedAlbumItemsPath(albumId), sharedAlbumItemsQueryToParams(query)),
    signal,
  );
}

export function listAlbumInvitations(signal?: AbortSignal): Promise<AlbumInvitation[]> {
  return apiGet<AlbumInvitation[]>(ALBUM_INVITATIONS_PATH, signal);
}

export function acceptAlbumInvitation(membershipId: string, signal?: AbortSignal): Promise<void> {
  return apiPost<void>(albumInvitationPath(membershipId, 'accept'), undefined, {
    signal,
  });
}

export function declineAlbumInvitation(membershipId: string, signal?: AbortSignal): Promise<void> {
  return apiPost<void>(albumInvitationPath(membershipId, 'decline'), undefined, {
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

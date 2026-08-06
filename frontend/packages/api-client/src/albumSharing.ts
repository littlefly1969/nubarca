import { api, ApiError } from './client';

// SHARE-ALBUM-01: live album sharing between authenticated NubArca users.
//
// Two families, mirroring the backend:
//   * /api/albums/{id}/members...  the OWNER managing who the album is shared
//     with.
//   * /api/shared-albums/...       the RECIPIENT's view of albums shared with
//     them, and their invitations.
//
// The recipient's media URLs are album-scoped and arrive ready-built from the
// server (`thumbnailUrl`, `previewUrl`, …). They carry no token and no
// signature: they are routes that are re-authorized on every request, so they
// are safe in an <img src> and useless to anybody without their own accepted
// membership. Never construct one by hand from a file id.

export type AlbumRole = 'viewer' | 'contributor' | 'editor';

export type AlbumMembershipState = 'pending' | 'accepted' | 'declined' | 'revoked';

// One row of the owner's member list. Identifies the person by DISPLAY NAME
// only — the API never returns another user's email address or user id, and
// `membershipId` is what addresses the row.
export interface AlbumMember {
  membershipId: string;
  displayName: string;
  // Masked account address ("m•••i@nubarca.local"), owner-only. Display names
  // are NOT unique, so without this an owner with two members called the same
  // thing cannot tell which one to revoke. Empty string when the stored address
  // is unusable. Never present in any recipient-facing shape.
  maskedEmail: string;
  role: AlbumRole;
  state: AlbumMembershipState;
  allowOriginalDownload: boolean;
  invitedAt: string;
  acceptedAt: string | null;
  declinedAt: string | null;
  revokedAt: string | null;
}

export interface ResolvedAlbumRecipient {
  displayName: string;
}

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
  // SHARE-ALBUM-03: the optimistic-concurrency token to echo on any editorial
  // mutation, and whether this caller may curate at all. The server enforces
  // both regardless — `canEdit` only decides whether to render the controls.
  version: number;
  canEdit: boolean;
}

// One item of an album as its OWNER sees it for moderation. Additive surface:
// contributions never appear in the owner's library, gallery or album
// workspace. `contributorDisplayName` / `contributorMaskedEmail` are null for
// the owner's own items and use the same privacy-safe disambiguation as the
// member list when present.
export interface AlbumContentItem {
  // The MEMBERSHIP row's stable id — what a reorder names, never the file.
  albumItemId: string;
  fileItemId: string;
  kind: 'image' | 'video';
  thumbnailUrl: string;
  origin: 'owner' | 'contribution';
  contributorDisplayName: string | null;
  contributorMaskedEmail: string | null;
  // 'unavailable' when the source was deleted, excluded, vaulted, or its
  // contributor's membership ended — the row is listed so the owner can clear
  // it, but nobody can open it.
  sourceState: 'available' | 'unavailable';
  addedAt: string;
  // True when this item is the album's CHOSEN cover. False for every item when
  // the album falls back to a derived one.
  isCover: boolean;
}

// The curator's moderation view, wrapped so the concurrency token travels with
// the items the caller is about to reorder or remove.
export interface AlbumContentResponse {
  version: number;
  coverFileItemId: string | null;
  canEdit: boolean;
  items: AlbumContentItem[];
}

// One media item of a shared album. Deliberately carries NO file name: a
// filename is owner-authored free text that can hold a person's name, and the
// viewer does not need it. `downloadUrl` is null unless the membership permits
// originals — and the endpoint enforces the same rule, so hiding the control is
// a courtesy, not the control.
export interface SharedAlbumItem {
  // The membership row's stable id, so a client holding this list can express a
  // reorder without conflating files and memberships.
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
  // True only for the caller's OWN contribution (they own the file and they
  // added it) — the same pair the server checks before accepting a withdrawal.
  // A capability, not an identity: the shared viewer never learns who
  // contributed the other items.
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

export async function listSharedAlbumItems(
  albumId: string,
  signal?: AbortSignal,
): Promise<SharedAlbumItem[]> {
  return api<SharedAlbumItem[]>(`/api/shared-albums/${albumId}/items`, { signal });
}

export async function listAlbumInvitations(signal?: AbortSignal): Promise<AlbumInvitation[]> {
  return api<AlbumInvitation[]>('/api/shared-albums/invitations', { signal });
}

export async function acceptAlbumInvitation(
  membershipId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/shared-albums/invitations/${membershipId}/accept`, {
    method: 'POST',
    signal,
  });
}

export async function declineAlbumInvitation(
  membershipId: string,
  signal?: AbortSignal,
): Promise<void> {
  await api<void>(`/api/shared-albums/invitations/${membershipId}/decline`, {
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

import { QueryBuilder, type QueryParams } from './query.ts';

// Album sharing between authenticated NubArca users (§25-§29).
//
// Two families, mirroring the backend:
//   * /api/albums/{id}/members...  the OWNER managing who the album is shared with
//   * /api/shared-albums/...       the RECIPIENT's view, and their invitations
//
// PRIVACY IS IN THE SHAPE, not only in the UI (§26). These types are why a
// client cannot leak what the server withholds:
//   * no `email` field exists on a member — only a MASKED address, owner-only;
//   * no `userId` exists anywhere;
//   * a membership row is addressed by `membershipId`, never by who it is;
//   * a recipient item carries no file NAME (owner-authored free text that can
//     hold a person's name) and no contributor identity.
//
// Recipient media URLs arrive READY-BUILT from the server and are album-scoped
// (§28). They carry no token and no signature: they are routes re-authorized on
// every request, safe in an <img src> and useless without an accepted
// membership of one's own. A client must never construct one from a file id.

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
  // PARTY-GUEST-MESSAGES-01: a narrow, owner-granted delegation to moderate
  // this album's party MESSAGES. NOT a role and not a party governance grant —
  // see setAlbumMemberPartyMessages.
  canManagePartyMessages: boolean;
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


/**
 * The ONLY dimension a shared album can be sliced on. It is answered from the
 * media kind the item shape already carries — a filter that needed
 * owner-private metadata would BE that metadata, leaked one question at a time.
 */
export type SharedAlbumItemKind = 'all' | 'image' | 'video';

export interface SharedAlbumItemsQuery {
  kind?: SharedAlbumItemKind;
  cursor?: string | null;
  limit?: number;
}

/**
 * AUTHORITY: NubArca.Api.Albums.Sharing.SharedAlbumItemsPage.
 *
 * There is no `hasMore` on the wire: a null `nextCursor` IS the end of the
 * listing. The mobile copy of this type used to declare one, promising a field
 * the server never sends — use `sharedAlbumHasMore` instead, so the derivation
 * is written once.
 */
export interface SharedAlbumItemsPage {
  items: SharedAlbumItem[];
  /** Null means "that was the last page", never "ask again". */
  nextCursor: string | null;
  /** The WHOLE album, whatever kind is being browsed, so a tab label does not
   * change meaning with the tab that is open. */
  total: number;
  photoCount: number;
  videoCount: number;
}

export function sharedAlbumHasMore(page: SharedAlbumItemsPage): boolean {
  return page.nextCursor !== null;
}

export function sharedAlbumItemsQueryToParams(query: SharedAlbumItemsQuery): QueryParams {
  const b = new QueryBuilder();
  if (query.kind !== undefined && query.kind !== 'all') b.set('kind', query.kind);
  b.setOptional('cursor', query.cursor);
  b.setNumber('limit', query.limit);
  return b.build();
}

// ── Routes and payloads (§43) ──────────────────────────────────────────────

export function albumMembersPath(albumId: string): string {
  return `/api/albums/${albumId}/members`;
}
export function albumMemberPath(albumId: string, membershipId: string): string {
  return `${albumMembersPath(albumId)}/${membershipId}`;
}
export function albumRecipientResolvePath(albumId: string): string {
  return `${albumMembersPath(albumId)}/resolve`;
}
export function albumMemberRolePath(albumId: string, membershipId: string): string {
  return `${albumMemberPath(albumId, membershipId)}/role`;
}
export function albumMemberDownloadPath(albumId: string, membershipId: string): string {
  return `${albumMemberPath(albumId, membershipId)}/download`;
}
export function albumMemberPartyMessagesPath(albumId: string, membershipId: string): string {
  return `${albumMemberPath(albumId, membershipId)}/party-messages`;
}

export const SHARED_ALBUMS_PATH = '/api/shared-albums';
export const ALBUM_INVITATIONS_PATH = '/api/shared-albums/invitations';
export function sharedAlbumPath(albumId: string): string {
  return `${SHARED_ALBUMS_PATH}/${albumId}`;
}
export function sharedAlbumItemsPath(albumId: string): string {
  return `${sharedAlbumPath(albumId)}/items`;
}
export function albumInvitationPath(membershipId: string, action: 'accept' | 'decline'): string {
  return `${ALBUM_INVITATIONS_PATH}/${membershipId}/${action}`;
}

/**
 * An invite names an EXACT address. There is no directory and no autocomplete
 * (§26): the owner types a full address, the server resolves it to a display
 * name for confirmation, and only then is an invitation created. A lookup that
 * accepted prefixes would be an account-enumeration oracle.
 */
export interface InviteAlbumMemberPayload {
  email: string;
  role: AlbumRole;
  allowOriginalDownload: boolean;
}

export interface SetAlbumMemberRolePayload { role: AlbumRole; }
export interface SetAlbumMemberDownloadPayload { allowOriginalDownload: boolean; }
/**
 * The narrow message-moderation delegation (§37). It is NOT a role, NOT party
 * administration and NOT a general moderation grant: it reaches this album's
 * party MESSAGES and nothing else, and only the owner may set it.
 */
export interface SetAlbumMemberPartyMessagesPayload { canManagePartyMessages: boolean; }

/** A membership that can still act. Pending, declined and revoked cannot. */
export function isActiveMembership(state: AlbumMembershipState): boolean {
  return state === 'accepted';
}

/** Whether a member row is history rather than a live grant (§25). */
export function isHistoricalMembership(state: AlbumMembershipState): boolean {
  return state === 'declined' || state === 'revoked';
}

/**
 * What a recipient's role lets them do. Derived in ONE place so no client
 * infers capability from a label (§27) — the server enforces all of it anyway.
 */
export interface SharedAlbumCapabilities {
  canView: boolean;
  canContribute: boolean;
  canEditCollaboratively: boolean;
  canDownloadOriginal: boolean;
  // NOTE: there is deliberately no `canWithdrawOwnContribution` here.
  // Withdrawal is decided PER ITEM, not per role — see canWithdrawItem below.
}

export function sharedAlbumCapabilities(input: {
  role: AlbumRole;
  allowOriginalDownload: boolean;
  canEdit: boolean;
}): SharedAlbumCapabilities {
  const contributes = input.role === 'contributor' || input.role === 'editor';
  return {
    canView: true,
    canContribute: contributes,
    // `canEdit` is the SERVER's answer, echoed. A role alone does not decide
    // it, which is why it is not computed from the role here.
    canEditCollaboratively: input.canEdit,
    canDownloadOriginal: input.allowOriginalDownload,
  };
}

/**
 * May THIS item be withdrawn?
 *
 * The answer is on the item, never on the role. `canWithdraw` is the server's
 * own conclusion — the caller owns the file and added it — and it survives a
 * role change: somebody downgraded from contributor to viewer can still take
 * back what they already contributed. Deriving it from the CURRENT role would
 * strand their own media in an album they can no longer contribute to.
 *
 * It is a capability, not an identity: the viewer never learns who contributed
 * the other items.
 */
export function canWithdrawItem(item: Pick<SharedAlbumItem, 'canWithdraw'>): boolean {
  return item.canWithdraw;
}

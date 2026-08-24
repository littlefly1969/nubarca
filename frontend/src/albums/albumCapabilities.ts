import type { AlbumRole } from '@nubarca/api-client';

// What the person looking at an album may DO with it — as one pure function,
// so the answer is testable without rendering anything and cannot be re-derived
// differently by two surfaces.
//
// The rule this encodes: a unified EXPERIENCE is not unified AUTHORITY. Owner
// and recipient see the same album, the same wall, the same viewer and the same
// Play; what differs is which actions exist at all. Actions a caller may not
// perform are ABSENT rather than disabled — a greyed-out "Delete" on somebody
// else's album still tells the recipient that deleting is a thing this screen
// knows how to do, and one regression away from being enabled.
//
// This is UX. Every capability below is independently enforced by the server,
// which answers a Viewer's mutation with a refusal whatever the client rendered.

export type AlbumOwnership = 'owner' | 'member';

export interface AlbumExperienceInput {
  ownership: AlbumOwnership;
  // The membership role. Null for the owner, who holds no membership at all.
  role: AlbumRole | null;
  // The SERVER's answer to "may this caller curate?". For a member it is
  // `SharedAlbumDetail.canEdit` and it WINS over the role: a role string is a
  // label, `canEdit` is the decision the backend actually made.
  canEdit: boolean;
  // Whether this membership permits originals. Per-item `downloadUrl` is still
  // the gate on any single item; this only says whether the album offers it.
  allowOriginalDownload: boolean;
}

export interface AlbumExperienceCapabilities {
  // ── Presentation: everybody who can open the album at all ────────────────
  browse: boolean;
  filterByKind: boolean;
  playback: boolean;
  // Sequential album playback. A pure viewer operation — it mutates nothing,
  // which is exactly why a Viewer gets it.
  play: boolean;
  download: boolean;

  // ── Membership-bounded ───────────────────────────────────────────────────
  contribute: boolean;
  withdrawOwnContribution: boolean;
  editAlbumDetails: boolean;
  curateContent: boolean;

  // ── Owner authority. Never true for a member, whatever their role ────────
  selectMedia: boolean;
  editMetadata: boolean;
  removeFromAlbum: boolean;
  exclude: boolean;
  trash: boolean;
  moveToPersonal: boolean;
  manageMembers: boolean;
  manageSettings: boolean;
  deleteAlbum: boolean;
  configureParty: boolean;
  showOnTv: boolean;
  peopleActions: boolean;
  similarityActions: boolean;
}

export function getAlbumExperienceCapabilities(
  input: AlbumExperienceInput,
): AlbumExperienceCapabilities {
  const isOwner = input.ownership === 'owner';
  // Contributing means "link media I own into an album somebody else owns". The
  // owner adding their own media is a different action on a different endpoint,
  // so it is not this capability.
  const canContribute = !isOwner && (input.role === 'contributor' || input.role === 'editor');
  // Curation is the server's decision, not the role's. An Editor whose
  // `canEdit` came back false gets nothing — which is the whole point of asking
  // the server instead of reading the label.
  const canCurate = isOwner || (input.ownership === 'member' && input.canEdit);

  return {
    browse: true,
    filterByKind: true,
    playback: true,
    play: true,
    // The owner is looking at their own media; a member needs the grant.
    download: isOwner || input.allowOriginalDownload,

    contribute: canContribute,
    // Still true for a Viewer: a contributor demoted to Viewer may take their
    // own media back out, and the per-item `canWithdraw` is what decides which
    // items that is. Offering it album-wide costs nothing and refusing it here
    // would strand somebody's media in an album they can no longer edit.
    withdrawOwnContribution: !isOwner,
    editAlbumDetails: canCurate,
    curateContent: canCurate,

    selectMedia: isOwner,
    editMetadata: isOwner,
    removeFromAlbum: isOwner,
    exclude: isOwner,
    trash: isOwner,
    moveToPersonal: isOwner,
    manageMembers: isOwner,
    manageSettings: isOwner,
    deleteAlbum: isOwner,
    configureParty: isOwner,
    showOnTv: isOwner,
    peopleActions: isOwner,
    similarityActions: isOwner,
  };
}

// The capabilities of one's OWN album. Stated once so no surface has to spell
// out the owner case by hand.
export const OWNER_ALBUM_CAPABILITIES: AlbumExperienceCapabilities =
  getAlbumExperienceCapabilities({
    ownership: 'owner', role: null, canEdit: true, allowOriginalDownload: true,
  });

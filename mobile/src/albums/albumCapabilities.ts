// What the person looking at an album may DO — one pure function, mirroring
// frontend/src/albums/albumCapabilities.ts. A unified EXPERIENCE is not
// unified AUTHORITY: actions a caller may not perform are ABSENT, not
// disabled, and every capability here is independently enforced by the server.
//
// Mobile v1 subset: browse/filter/playback/play/download plus the two
// membership-bounded actions (contribute, withdraw own contribution). All the
// owner-authority flags are still produced so a card model can branch on them
// without re-deriving roles.

import type { AlbumRole } from '../api/sharedAlbums.ts';

export type AlbumOwnership = 'owner' | 'member';

export interface AlbumExperienceInput {
  ownership: AlbumOwnership;
  role: AlbumRole | null;
  canEdit: boolean;
  allowOriginalDownload: boolean;
}

export interface AlbumExperienceCapabilities {
  browse: boolean;
  filterByKind: boolean;
  playback: boolean;
  play: boolean;
  download: boolean;
  contribute: boolean;
  withdrawOwnContribution: boolean;
  editAlbumDetails: boolean;
  curateContent: boolean;
  deleteAlbum: boolean;
  addToAlbum: boolean;
}

export function getAlbumExperienceCapabilities(
  input: AlbumExperienceInput,
): AlbumExperienceCapabilities {
  const isOwner = input.ownership === 'owner';
  const canContribute =
    !isOwner && (input.role === 'contributor' || input.role === 'editor');

  return {
    browse: true,
    filterByKind: true,
    playback: true,
    play: true,
    // Owner browses their own media; a member needs the membership grant.
    // Per-item downloadUrl remains the gate on any single item.
    download: isOwner || input.allowOriginalDownload,

    contribute: canContribute,
    // Still true for a demoted Viewer: per-item canWithdraw decides which of
    // THEIR OWN items they can take back out.
    withdrawOwnContribution: !isOwner,
    editAlbumDetails: isOwner || (input.ownership === 'member' && input.canEdit),
    curateContent: isOwner || (input.ownership === 'member' && input.canEdit),
    deleteAlbum: isOwner,
    addToAlbum: isOwner,
  };
}

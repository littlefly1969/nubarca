// What a selection may DO (§22, §38).
//
// UI availability is decided by capabilities, never by device type: the phone
// and the browser ask this same function and each draws the answer its own way
// — a bottom sheet, a menu. `if (isMobile)` is exactly the shape this replaces.
//
// Pure and exhaustively testable, so the library and album surfaces cannot
// drift apart, and so a photo-only destination can never run partially over a
// mixed selection.
//
// Rules:
//   * Add to album / Move to Personal / Move to Trash — any non-empty selection
//     of normal owner-scoped media.
//   * Move to Excluded — Active scope only.
//   * Restore to library — Excluded scope only.
//   * Remove from THIS album — album source only.
//   * Photo-only destinations (Beauty Lab, Plates, ...) — only when the
//     selection is ENTIRELY images; never for a mixed or all-video selection.
//   * Print — at least one photograph, in the library or an own album, Active
//     scope only. Videos in the selection are left out of the print, never a
//     reason to refuse it; the studio offers only the formats the photographs
//     fill. More than PRINT_SELECTION_MAX photographs is refused with a notice
//     where the action runs, not by hiding it.
//
// Only currently-existing destinations appear here. A future one is added when
// it exists, not in anticipation of it.

import type { MediaItem, MediaGalleryScope } from './media.ts';

/** The most photographs one print session takes (their ids travel in the studio's URL). */
export const PRINT_SELECTION_MAX = 100;

export type MediaWorkspaceSourceKind = 'library' | 'album';

export interface MediaSelectionCapabilities {
  allImages: boolean;
  allVideos: boolean;
  mixed: boolean;
  canAddToAlbum: boolean;
  canMoveToPersonal: boolean;
  canMoveToExcluded: boolean;
  canRestore: boolean;
  canTrash: boolean;
  canRemoveFromCurrentAlbum: boolean;
  canUsePhotoOnlyDestinations: boolean;
  canPrintPhoto: boolean;
}

export interface CapabilityInput {
  items: readonly Pick<MediaItem, 'kind'>[];
  source: MediaWorkspaceSourceKind;
  scope: MediaGalleryScope;
}

export function getMediaSelectionCapabilities(
  { items, source, scope }: CapabilityInput,
): MediaSelectionCapabilities {
  const count = items.length;
  const hasAny = count > 0;
  const allImages = hasAny && items.every((it) => it.kind === 'image');
  const allVideos = hasAny && items.every((it) => it.kind === 'video');
  const mixed = hasAny && !allImages && !allVideos;

  return {
    allImages,
    allVideos,
    mixed,
    canAddToAlbum: hasAny,
    canMoveToPersonal: hasAny,
    canMoveToExcluded: hasAny && scope === 'active',
    canRestore: hasAny && scope === 'excluded',
    canTrash: hasAny,
    canRemoveFromCurrentAlbum: hasAny && source === 'album',
    canUsePhotoOnlyDestinations: allImages,
    // At least one photograph; the studio composes its sheets from them.
    canPrintPhoto: items.some((it) => it.kind === 'image') && scope === 'active',
  };
}

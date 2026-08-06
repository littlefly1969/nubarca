import type { MediaItem } from '@nubarca/api-client';
import type { MediaLibraryScope } from './mediaWorkspaceQuery';

// Pure capability matrix for the workspace selection bar. Given the currently
// selected items (resolved from the loaded accumulator), the source and the
// scope, it decides which bulk actions are offered. Kept free of React so it is
// exhaustively unit-testable and cannot drift between the library and album
// surfaces.
//
// Rules (see the slice spec):
//   * Add to album / Move to Personal / Move to Trash — any non-empty selection
//     of normal owner-scoped media.
//   * Move to Excluded — Active scope only.
//   * Restore to library — Excluded scope only.
//   * Remove from THIS album — album source only.
//   * Photo-only destinations (Beauty Lab, Plates, …) — only when the selection
//     is ENTIRELY images; never for a mixed or all-video selection (a photo-only
//     action must never run partially).

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
}

export interface CapabilityInput {
  items: readonly Pick<MediaItem, 'kind'>[];
  source: MediaWorkspaceSourceKind;
  scope: MediaLibraryScope;
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
  };
}

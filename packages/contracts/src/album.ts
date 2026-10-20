// Albums: the owner's own album vocabulary.
//
// AUTHORITY: NubArca.Api.Albums. The membership rule this encodes, and which
// every client must present truthfully (§24):
//
//   removing an item from an album  !=  deleting it from the library
//
// DELETE on /api/albums/{id}/items(/bulk) changes MEMBERSHIP only; it never
// touches the FileItem or its blob. DELETE /api/albums/{id} deletes the album,
// not its media.

/** One tile of an album card's cover mosaic. */
export interface AlbumCoverItem {
  fileItemId: string;
  kind: 'image' | 'video';
  /** Small thumbnail for images, poster for videos. */
  thumbnailUrl: string;
}

export interface AlbumSummary {
  id: string;
  name: string;
  description: string | null;
  /** Raw membership count. */
  itemCount: number;
  showOnTv: boolean;
  createdAt: string;
  updatedAt: string;
  /** Per-kind ACTIVE counts, plus excluded members and the cover mosaic (<=4). */
  photoCount: number;
  videoCount: number;
  excludedCount: number;
  coverItems: AlbumCoverItem[];
}

/**
 * The album's sequence: its photographs by date taken, newest first ('desc')
 * unless the owner turned it around ('asc'). The owner's choice is the order
 * every member, TV and party wall gets; there is no manual order.
 */
export type AlbumSortDirection = 'asc' | 'desc';

export interface AlbumDetail {
  id: string;
  name: string;
  description: string | null;
  showOnTv: boolean;
  createdAt: string;
  updatedAt: string;
  sortDirection: AlbumSortDirection;
  // The optimistic-concurrency token the cover and curation routes echo.
  version: number;
  // The chosen cover, or null when the album derives one.
  coverFileItemId: string | null;
}

export interface SetAlbumSortPayload {
  direction: AlbumSortDirection;
}

export function albumSortPath(albumId: string): string {
  return `/api/albums/${albumId}/sort`;
}

export interface AlbumItemSummary {
  fileItemId: string;
  name: string;
  mimeType: string;
  sizeBytes: number;
  addedAt: string;
  thumbnailUrl: string | null;
}

/**
 * Counts-only result of a bulk add/remove.
 *
 * It never reveals WHICH ids were foreign or missing: that would let a caller
 * probe another owner's library for the existence of a file id.
 */
export interface BulkAlbumItemsResult {
  requested: number;
  succeeded: number;
  skipped: number;
}

// ── Routes and payloads (§43) ──────────────────────────────────────────────
// The mutation payloads are canonical too. A client may wrap the transport
// however it likes; what goes in the body may not drift.

export const ALBUMS_PATH = '/api/albums';
export function albumPath(albumId: string): string {
  return `${ALBUMS_PATH}/${albumId}`;
}
export function albumItemsPath(albumId: string): string {
  return `${albumPath(albumId)}/items`;
}
export function albumItemsBulkPath(albumId: string): string {
  return `${albumItemsPath(albumId)}/bulk`;
}

export interface CreateAlbumPayload {
  name: string;
  description: string | null;
}
export function createAlbumPayload(
  name: string,
  description?: string | null,
): CreateAlbumPayload {
  return { name, description: description ?? null };
}

/** Only the keys the caller actually intends to change are sent. */
export interface UpdateAlbumPayload {
  name?: string;
  description?: string | null;
  showOnTv?: boolean;
}

/** Album TV visibility is its own route, not part of the general update. */
export function albumTvSettingsPath(albumId: string): string {
  return `${albumPath(albumId)}/tv-settings`;
}
export interface AlbumTvVisibilityPayload {
  showOnTv: boolean;
}

export interface AlbumItemsPayload {
  fileItemIds: string[];
}
export function albumItemsPayload(fileItemIds: readonly string[]): AlbumItemsPayload {
  return { fileItemIds: [...fileItemIds] };
}

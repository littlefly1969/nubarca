// The unified media surface: GET /api/media, GET /api/albums/{albumId}/media
// and GET /api/media/semantic.
//
// AUTHORITY: NubArca.Api.Files.MediaItem. Not "whatever the web client had" —
// converging this found the two clients had drifted against the server in
// OPPOSITE directions, so the server record is what is transcribed here.
//
// No storage internals ever appear: no BlobObjectId, StorageKey, SHA,
// embeddings or raw metadata. GPS is presence-only, never coordinates.

import { QueryBuilder, type QueryParams } from './query.ts';

export type MediaKind = 'image' | 'video';

// The WIRE values, exactly as GalleryQueryParser accepts them. `datetaken` is
// lower-case on purpose: it is the server's spelling, and a camel-cased
// `dateTaken` is rejected with a 400.
export type MediaSortField = 'created' | 'name' | 'size' | 'datetaken';
export type MediaSortDirection = 'asc' | 'desc';
/** Which library scope a listing reads from. The server accepts these two and
 * nothing else — there is no 'all'. */
export type MediaGalleryScope = 'active' | 'excluded';
/** Album-membership filter: any / in some album / in none. */
export type AlbumMembership = 'any' | 'assigned' | 'unassigned';
/** How multiple included people combine. */
export type PeopleMatchMode = 'all' | 'any';

interface MediaItemBase {
  id: string;
  kind: MediaKind;
  name: string;
  title: string | null;
  displayName: string;
  mimeType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  updatedAt: string | null;
  takenAt: string | null;
  favorite: boolean;
  rating: number | null;
  /** Small thumbnail (image) or poster (video). */
  thumbnailUrl: string;
  occurrenceCount: number;
  hasDuplicates: boolean;
}

export interface ImageMediaItem extends MediaItemBase {
  kind: 'image';
  /** Presence only. Coordinates are never on this surface. */
  hasGps: boolean | null;
}

export interface VideoMediaItem extends MediaItemBase {
  kind: 'video';
  posterUrl: string | null;
  durationSeconds: number | null;
  videoCodec: string | null;
  hasAudio: boolean | null;
  posterSource: string | null;
  previewStripUrl: string | null;
  // NOTE: audioCodec and frameRate are deliberately ABSENT. They exist on
  // VideoItem (/api/videos), not on MediaItem, and the mobile client used to
  // declare them here — a type that promised fields the server never sends.
}

export type MediaItem = ImageMediaItem | VideoMediaItem;

export interface MediaListResponse {
  items: MediaItem[];
  limit: number;
  count: number;
  nextCursor: string | null;
  hasMore: boolean;
  total: number;
  photoCount: number;
  videoCount: number;
}

/**
 * The unified wire query.
 *
 * Photo params are valid only with kind=image and video params only with
 * kind=video: the backend 400s an incompatible combination, so a caller must
 * not set them under the wrong kind.
 */
export interface ListMediaQuery {
  scope?: MediaGalleryScope;
  kind: 'all' | 'image' | 'video';
  q?: string;
  favorite?: boolean;
  minRating?: number;
  dateTakenFrom?: string;
  dateTakenTo?: string;
  albumMembership?: AlbumMembership;
  sort?: MediaSortField;
  direction?: MediaSortDirection;
  limit?: number;
  cursor?: string | null;
  // ---- photo-only (kind=image) ----
  hasGps?: boolean;
  collapseDuplicates?: boolean;
  similarTo?: string;
  includePeople?: string[];
  excludePeople?: string[];
  includePeopleMode?: PeopleMatchMode;
  // ---- video-only (kind=video) ----
  durationMin?: number;
  durationMax?: number;
  minHeight?: number;
  codec?: string;
  hasAudio?: boolean;
}

/**
 * THE serialization. Every client must produce these exact pairs, in this
 * exact order, for a given query — that equality is what the parity tests
 * assert.
 *
 * Two defaults are deliberately omitted rather than sent: `scope=active` and
 * `albumMembership=any` mean "no filter", and sending them would make an
 * unfiltered request look filtered in logs and caches.
 */
export function mediaQueryToParams(query: ListMediaQuery): QueryParams {
  const b = new QueryBuilder();
  b.set('kind', query.kind);
  if (query.scope !== undefined && query.scope !== 'active') b.set('scope', query.scope);
  b.setOptional('q', query.q);
  b.setBool('favorite', query.favorite);
  b.setNumber('minRating', query.minRating);
  b.setOptional('dateTakenFrom', query.dateTakenFrom);
  b.setOptional('dateTakenTo', query.dateTakenTo);
  if (query.albumMembership !== undefined && query.albumMembership !== 'any') {
    b.set('albumMembership', query.albumMembership);
  }
  b.setOptional('sort', query.sort);
  b.setOptional('direction', query.direction);
  b.setNumber('limit', query.limit);
  b.setOptional('cursor', query.cursor);
  b.setBool('hasGps', query.hasGps);
  // Only the enabling value is meaningful; `false` is the default.
  if (query.collapseDuplicates === true) b.set('collapseDuplicates', 'true');
  b.setOptional('similarTo', query.similarTo);
  b.setIdList('includePeople', query.includePeople);
  b.setIdList('excludePeople', query.excludePeople);
  b.setOptional('includePeopleMode', query.includePeopleMode);
  b.setNumber('durationMin', query.durationMin);
  b.setNumber('durationMax', query.durationMax);
  b.setNumber('minHeight', query.minHeight);
  b.setOptional('codec', query.codec);
  b.setBool('hasAudio', query.hasAudio);
  return b.build();
}

/** Route for the library listing. */
export const MEDIA_LIST_PATH = '/api/media';
/** Route for one album's listing. */
export function albumMediaPath(albumId: string): string {
  return `/api/albums/${albumId}/media`;
}

// ── Semantic search ────────────────────────────────────────────────────────
// A conceptually DIFFERENT backend operation from physical filtering, and it
// stays a separate route with a separate query on purpose (§10). One shared
// definition per operation is the rule — not one endpoint for everything.

/** One temporal match inside a video's own timeline (whole milliseconds). */
export interface SemanticBestMatch {
  evidenceType: string;
  startMilliseconds: number | null;
  endMilliseconds: number | null;
  representativeMilliseconds: number | null;
}

export interface SemanticMediaResultItem {
  media: MediaItem;
  bestMatch: SemanticBestMatch;
  /** Up to three further distinct intervals, best-first (videos only). */
  additionalMatches: SemanticBestMatch[];
}

export interface SemanticMediaSearchResponse {
  items: SemanticMediaResultItem[];
  nextCursor: string | null;
  hasMore: boolean;
  semanticStatus: 'ok' | 'indexing';
  total: number;
}

/** Filters the semantic route understands. The rest are not semantic-aware. */
export interface SearchSemanticMediaQuery {
  q: string;
  kind: 'all' | 'image' | 'video';
  limit?: number;
  cursor?: string | null;
  favorite?: boolean;
  minRating?: number;
  dateTakenFrom?: string;
  dateTakenTo?: string;
  /** A PHYSICAL filter: applied to the candidate scope before ranking, and it
   * binds the cursor and the ranking cache. */
  albumMembership?: AlbumMembership;
}

export function semanticMediaQueryToParams(query: SearchSemanticMediaQuery): QueryParams {
  const b = new QueryBuilder();
  b.set('q', query.q);
  b.set('kind', query.kind);
  b.setNumber('limit', query.limit);
  b.setOptional('cursor', query.cursor);
  b.setBool('favorite', query.favorite);
  b.setNumber('minRating', query.minRating);
  b.setOptional('dateTakenFrom', query.dateTakenFrom);
  b.setOptional('dateTakenTo', query.dateTakenTo);
  if (query.albumMembership !== undefined && query.albumMembership !== 'any') {
    b.set('albumMembership', query.albumMembership);
  }
  return b.build();
}

export const MEDIA_SEMANTIC_PATH = '/api/media/semantic';

// ── File lifecycle (§21) ───────────────────────────────────────────────────
// Moving to Trash is a SOFT delete: the FileItem leaves the library listings
// and can be restored. It is not the same verb as removing an item from an
// album, which only changes membership (see album.ts).

export function fileItemPath(fileItemId: string): string {
  return `/api/files/${fileItemId}`;
}
export function fileRestorePath(fileItemId: string): string {
  return `/api/files/${fileItemId}/restore`;
}

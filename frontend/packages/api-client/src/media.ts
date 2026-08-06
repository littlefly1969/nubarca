import { api } from './client';
import type { ImageSortDirection, ImageSortField, AlbumMembership, MediaGalleryScope } from './images';

// Slice 5: unified media-workspace client for GET /api/media and
// GET /api/albums/{albumId}/media. One discriminated item type carries both
// images and videos so the "Tutti" tab renders a mixed, server-ordered grid.
// Mirrors NubArca.Api.Files.MediaItem. No storage internals (no BlobObjectId,
// StorageKey, SHA, embeddings, raw metadata); GPS is presence-only.

export type MediaKind = 'image' | 'video';

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
  thumbnailUrl: string; // small thumbnail (image) or poster (video)
  occurrenceCount: number;
  hasDuplicates: boolean;
}

export interface ImageMediaItem extends MediaItemBase {
  kind: 'image';
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
}

export type MediaItem = ImageMediaItem | VideoMediaItem;

// Mirrors NubArca.Api.Files.MediaListResponse. `total` is the server-
// authoritative filtered total (paging-independent, duplicate-collapse aware);
// `photoCount`/`videoCount` split it by kind for the tab labels.
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

// The unified wire query. Photo params are valid only with kind=image and video
// params only with kind=video — the backend 400s an incompatible combination,
// so callers must not set them under the wrong kind. This is the type the pure
// workspace query model (mediaWorkspaceQuery.queryToWire) produces.
export interface ListMediaQuery {
  scope?: MediaGalleryScope;
  kind: 'all' | 'image' | 'video';
  q?: string;
  favorite?: boolean;
  minRating?: number;
  dateTakenFrom?: string;
  dateTakenTo?: string;
  albumMembership?: AlbumMembership;
  sort?: ImageSortField;
  direction?: ImageSortDirection;
  limit?: number;
  cursor?: string;
  // Photo-only (kind=image)
  hasGps?: boolean;
  collapseDuplicates?: boolean;
  similarTo?: string;
  includePeople?: string[];
  excludePeople?: string[];
  includePeopleMode?: 'all' | 'any';
  // Video-only (kind=video)
  durationMin?: number;
  durationMax?: number;
  minHeight?: number;
  codec?: string;
  hasAudio?: boolean;
}

function toParams(query: ListMediaQuery): URLSearchParams {
  const p = new URLSearchParams();
  p.set('kind', query.kind);
  if (query.scope !== undefined && query.scope !== 'active') p.set('scope', query.scope);
  if (query.q) p.set('q', query.q);
  if (query.favorite !== undefined) p.set('favorite', String(query.favorite));
  if (query.minRating !== undefined) p.set('minRating', String(query.minRating));
  if (query.dateTakenFrom) p.set('dateTakenFrom', query.dateTakenFrom);
  if (query.dateTakenTo) p.set('dateTakenTo', query.dateTakenTo);
  if (query.albumMembership && query.albumMembership !== 'any') {
    p.set('albumMembership', query.albumMembership);
  }
  if (query.sort) p.set('sort', query.sort);
  if (query.direction) p.set('direction', query.direction);
  if (query.limit !== undefined) p.set('limit', String(query.limit));
  if (query.cursor) p.set('cursor', query.cursor);
  if (query.hasGps !== undefined) p.set('hasGps', String(query.hasGps));
  if (query.collapseDuplicates) p.set('collapseDuplicates', 'true');
  if (query.similarTo) p.set('similarTo', query.similarTo);
  if (query.includePeople && query.includePeople.length > 0) {
    p.set('includePeople', query.includePeople.join(','));
  }
  if (query.excludePeople && query.excludePeople.length > 0) {
    p.set('excludePeople', query.excludePeople.join(','));
  }
  if (query.includePeopleMode) p.set('includePeopleMode', query.includePeopleMode);
  if (query.durationMin !== undefined) p.set('durationMin', String(query.durationMin));
  if (query.durationMax !== undefined) p.set('durationMax', String(query.durationMax));
  if (query.minHeight !== undefined) p.set('minHeight', String(query.minHeight));
  if (query.codec) p.set('codec', query.codec);
  if (query.hasAudio !== undefined) p.set('hasAudio', String(query.hasAudio));
  return p;
}

export function listMedia(query: ListMediaQuery, signal?: AbortSignal): Promise<MediaListResponse> {
  const qs = toParams(query).toString();
  return api<MediaListResponse>(`/api/media${qs ? `?${qs}` : ''}`, { signal });
}

export function listAlbumMedia(
  albumId: string,
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  const qs = toParams(query).toString();
  return api<MediaListResponse>(`/api/albums/${albumId}/media${qs ? `?${qs}` : ''}`, { signal });
}

// ---------------------------------------------------------------------------
// VSEM-03: unified photo+video semantic search (GET /api/media/semantic).
// One relevance-ranked stream across both kinds; video results carry bounded
// TEMPORAL EVIDENCE (their best matching segment + a representative
// timestamp). No scores, vectors or internal identifiers are ever on the wire.

// One temporal match inside a video's own timeline (whole milliseconds).
// Photos carry the null-temporal variant.
export interface SemanticBestMatch {
  evidenceType: string; // currently always 'visual'
  startMilliseconds: number | null;
  endMilliseconds: number | null;
  representativeMilliseconds: number | null;
}

export interface SemanticMediaResultItem {
  media: MediaItem;
  bestMatch: SemanticBestMatch;
  // Up to three further distinct intervals, best-first (videos only).
  additionalMatches: SemanticBestMatch[];
}

export interface SemanticMediaSearchResponse {
  items: SemanticMediaResultItem[];
  nextCursor: string | null;
  hasMore: boolean;
  semanticStatus: 'ok' | 'indexing';
  total: number;
}

// Supported filters on the semantic route: favorite, minRating, the DateTaken
// range and album membership. Other gallery filters are not semantic-aware yet.
export interface SearchSemanticMediaQuery {
  q: string;
  kind: 'all' | 'image' | 'video';
  limit?: number;
  cursor?: string;
  favorite?: boolean;
  minRating?: number;
  dateTakenFrom?: string;
  dateTakenTo?: string;
  // SEARCH-SEM-01: a PHYSICAL filter — the server applies it to the candidate
  // scope before ranking, and it binds the cursor and ranking cache.
  albumMembership?: AlbumMembership;
}

export function searchSemanticMedia(
  query: SearchSemanticMediaQuery,
  signal?: AbortSignal,
): Promise<SemanticMediaSearchResponse> {
  const p = new URLSearchParams();
  p.set('q', query.q);
  p.set('kind', query.kind);
  if (query.limit !== undefined) p.set('limit', String(query.limit));
  if (query.cursor) p.set('cursor', query.cursor);
  if (query.favorite !== undefined) p.set('favorite', String(query.favorite));
  if (query.minRating !== undefined) p.set('minRating', String(query.minRating));
  if (query.dateTakenFrom) p.set('dateTakenFrom', query.dateTakenFrom);
  if (query.dateTakenTo) p.set('dateTakenTo', query.dateTakenTo);
  if (query.albumMembership && query.albumMembership !== 'any') {
    p.set('albumMembership', query.albumMembership);
  }
  return api<SemanticMediaSearchResponse>(`/api/media/semantic?${p.toString()}`, { signal });
}

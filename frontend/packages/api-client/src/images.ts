import { api } from './client';

// Mirrors NubArca.Api.Files.ImageItem on the backend. `thumbnailUrl` points
// at the existing /api/files/{id}/thumbnail?size=small endpoint; clients hit
// it directly via <img src>. The URL may 404 for images whose thumbnail was
// skipped (corrupt source, too large, etc.) — the gallery shows a placeholder
// in that case.
export interface ImageItem {
  id: string;
  // Logical file name. Still the download / diagnostic name; a title never
  // renames the file, so this stays available even when a title is set.
  name: string;
  // Owner-scoped user title, null when unset.
  title: string | null;
  // What the UI renders: title when present, else name.
  displayName: string;
  mimeType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  updatedAt: string | null;
  thumbnailUrl: string;
  // Slice 75: 1 when no duplicates or collapseDuplicates not requested.
  // > 1 means this blob appears N times in the user's library.
  // Never exposes SHA-256, BlobObjectId, or internal IDs.
  occurrenceCount: number;
  hasDuplicates: boolean;
}

// Slice 75: a single logical occurrence of a duplicated file.
// Returned by GET /api/files/{id}/duplicates.
export interface DuplicateOccurrence {
  fileItemId: string;
  name: string;
  parentFolderId: string | null;
  mimeType: string;
  sizeBytes: number;
  createdAt: string;
  updatedAt: string | null;
}

export function listDuplicateOccurrences(
  fileItemId: string,
  signal?: AbortSignal,
): Promise<DuplicateOccurrence[]> {
  return api<DuplicateOccurrence[]>(`/api/files/${fileItemId}/duplicates`, { signal });
}

// Mirrors NubArca.Api.Files.ImageListResponse. `count` is the size of the
// current page (not a total), as the backend deliberately avoids COUNT(*).
// Slice 60: `nextCursor` is set when more results exist; pass it back via
// `cursor` on the next request. `hasMore` is the same signal as
// `nextCursor !== null`, exposed explicitly for clients that prefer a flag.
export interface ImageListResponse {
  items: ImageItem[];
  limit: number;
  offset: number;
  count: number;
  nextCursor: string | null;
  hasMore: boolean;
  // Slice 100: present only on a physical-filter-first, semantic-ranked page
  // (?semanticQuery=…). semanticStatus: 'ok' | 'unavailable' | 'indexing'.
  semanticActive?: boolean;
  semanticTopK?: number;
  semanticStatus?: 'ok' | 'unavailable' | 'indexing' | null;
  // Server-authoritative total of items matching the current filter set
  // (paging-independent; duplicate-collapse aware). On the semantic path this is
  // the reduced result-set size (≤ Top-K). Null/undefined on the legacy offset
  // path. Clients must show this — never the loaded-page count.
  total?: number | null;
}

export type ImageSortField = 'created' | 'name' | 'size' | 'datetaken';
export type ImageSortDirection = 'asc' | 'desc';

// Shared by the photo and video galleries — one backend concept, one wire
// vocabulary. Mirrors NubArca.Api.Files.AlbumMembershipFilter.
export type AlbumMembership = 'any' | 'assigned' | 'unassigned';

export interface ListImagesQuery {
  q?: string;
  sort?: ImageSortField;
  direction?: ImageSortDirection;
  limit?: number;
  // Cursor and offset are mutually exclusive on the backend. Prefer `cursor`
  // for forward pagination (slice 60). `offset` is kept for backwards
  // compatibility.
  offset?: number;
  cursor?: string;
  folderId?: string;
  // Slice 61: compact discovery filters. Any change to these resets cursor
  // pagination on the frontend (the backend will 400 a stale cursor too).
  favorite?: boolean;
  minRating?: number;
  hasGps?: boolean;
  dateTakenFrom?: string; // ISO-8601 UTC
  dateTakenTo?: string;
  // Slice 75: collapse identical blobs to one representative per page,
  // annotated with occurrenceCount. Included in the cursor fingerprint
  // so changing this resets pagination.
  collapseDuplicates?: boolean;
  // Gallery-as-operational-surface filters (owner-private). `albumId`
  // constrains the gallery to one album; `similarTo` restricts it to the
  // owner-scoped similar set of a file; `includePeople`/`excludePeople` are
  // owner-private clustered-person ids with `includePeopleMode` ('all'|'any').
  // Any change resets cursor pagination (folded into the backend fingerprint).
  albumId?: string;
  // Album MEMBERSHIP, not a specific album: 'assigned' = in at least one album,
  // 'unassigned' = in none, 'any' (or omitted) = no constraint. Combining
  // `albumId` with 'unassigned' is contradictory and the backend 400s.
  albumMembership?: AlbumMembership;
  similarTo?: string;
  includePeople?: string[];
  excludePeople?: string[];
  includePeopleMode?: 'all' | 'any';
  // Slice 100: optional visual semantic residual + server-clamped Top-K. When
  // set, the owner gallery becomes physical-filter-first + semantic-ranked.
  semanticQuery?: string;
  semanticTopK?: number;
  // Slice 3 (media organization): which media-library scope to list. 'active'
  // (default) is the normal gallery; 'excluded' is the "Esclusi" tab. Part of
  // the backend cursor fingerprint, so switching scope resets pagination.
  mediaScope?: MediaGalleryScope;
}

// Slice 3: the two media-library scopes an ordinary gallery view can request.
// 'all' is an internal backend scope never exposed to the frontend.
export type MediaGalleryScope = 'active' | 'excluded';

export function listImages(
  query: ListImagesQuery = {},
  signal?: AbortSignal,
): Promise<ImageListResponse> {
  const params = new URLSearchParams();
  if (query.q !== undefined && query.q.length > 0) params.set('q', query.q);
  if (query.sort !== undefined) params.set('sort', query.sort);
  if (query.direction !== undefined) params.set('direction', query.direction);
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  if (query.offset !== undefined) params.set('offset', String(query.offset));
  if (query.cursor !== undefined && query.cursor.length > 0) {
    params.set('cursor', query.cursor);
  }
  if (query.folderId !== undefined) params.set('folderId', query.folderId);
  if (query.favorite !== undefined) params.set('favorite', String(query.favorite));
  if (query.minRating !== undefined) params.set('minRating', String(query.minRating));
  if (query.hasGps !== undefined) params.set('hasGps', String(query.hasGps));
  if (query.dateTakenFrom !== undefined && query.dateTakenFrom.length > 0) {
    params.set('dateTakenFrom', query.dateTakenFrom);
  }
  if (query.dateTakenTo !== undefined && query.dateTakenTo.length > 0) {
    params.set('dateTakenTo', query.dateTakenTo);
  }
  if (query.collapseDuplicates === true) {
    params.set('collapseDuplicates', 'true');
  }
  if (query.albumId !== undefined && query.albumId.length > 0) {
    params.set('albumId', query.albumId);
  }
  if (query.albumMembership !== undefined && query.albumMembership !== 'any') {
    params.set('albumMembership', query.albumMembership);
  }
  if (query.similarTo !== undefined && query.similarTo.length > 0) {
    params.set('similarTo', query.similarTo);
  }
  if (query.includePeople !== undefined && query.includePeople.length > 0) {
    params.set('includePeople', query.includePeople.join(','));
  }
  if (query.excludePeople !== undefined && query.excludePeople.length > 0) {
    params.set('excludePeople', query.excludePeople.join(','));
  }
  if (query.includePeopleMode !== undefined) {
    params.set('includePeopleMode', query.includePeopleMode);
  }
  if (query.semanticQuery !== undefined && query.semanticQuery.length > 0) {
    params.set('semanticQuery', query.semanticQuery);
    if (query.semanticTopK !== undefined) params.set('semanticTopK', String(query.semanticTopK));
  }
  if (query.mediaScope !== undefined && query.mediaScope !== 'active') {
    params.set('mediaScope', query.mediaScope);
  }

  const qs = params.toString();
  const path = qs.length === 0 ? '/api/images' : `/api/images?${qs}`;
  return api<ImageListResponse>(path, { signal });
}

// --- Natural-language command interpretation (LOCAL; owner-authenticated) ---
// The command + current filter state are POSTed; the server returns a validated
// PROPOSED draft the user must explicitly apply. The command is never persisted,
// never in a URL, never in browser storage. Reuses the SAME backend service as
// the TV surface; owner is the authenticated principal (no TV session/grant).

export interface GalleryCurrentFilterState {
  peopleInclude: string[];
  peopleExclude: string[];
  peopleMatch: 'all' | 'any';
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateTakenFrom: string | null;
  dateTakenTo: string | null;
  collapseDuplicates: boolean | null;
  sort: string | null;
  sortDirection: string | null;
  metadataSearch: string | null;
  semanticQuery: string | null;
}

export interface GalleryInterpretDraft {
  version: number;
  operation: 'replace' | 'refine' | 'clear';
  peopleInclude: string[];
  peopleExclude: string[];
  peopleMatch: 'all' | 'any';
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateTakenFrom: string | null;
  dateTakenTo: string | null;
  collapseDuplicates: boolean | null;
  sort: string | null;
  sortDirection: string | null;
  metadataSearch: string | null;
  semanticQuery: string | null;
  semanticQueryEnglish: string | null;
  semanticTopK: number;
}

export interface GalleryPersonAmbiguity {
  text: string;
  mode: 'include' | 'exclude';
  candidates: { personId: string; name: string | null; faceCount: number }[];
}

export interface GalleryInterpretResponse {
  draft: GalleryInterpretDraft;
  resolvedPeople: { text: string; mode: 'include' | 'exclude'; personId: string; name: string | null }[];
  ambiguities: GalleryPersonAmbiguity[];
  warnings: string[];
  requiresClarification: boolean;
}

export interface GalleryInterpretRequest {
  command: string;
  locale: string;
  timeZone: string;
  currentDate?: string;
  currentFilters: GalleryCurrentFilterState;
}

export type GalleryInterpretErrorKind =
  | 'unsupported' | 'busy' | 'timeout' | 'unavailable' | 'failed' | 'auth';

export class GalleryInterpretError extends Error {
  constructor(public readonly kind: GalleryInterpretErrorKind) {
    super(kind);
    this.name = 'GalleryInterpretError';
  }
}

export async function interpretGalleryCommand(
  request: GalleryInterpretRequest,
  signal?: AbortSignal,
): Promise<GalleryInterpretResponse> {
  try {
    return await api<GalleryInterpretResponse>('/api/images/interpret-command', {
      method: 'POST',
      json: request,
      signal,
    });
  } catch (err) {
    const status = (err as { status?: number }).status;
    throw new GalleryInterpretError(
      status === 401 || status === 403 ? 'auth'
        : status === 422 ? 'unsupported'
        : status === 429 ? 'busy'
        : status === 503 ? 'unavailable'
        : status === 504 ? 'timeout'
        : 'failed',
    );
  }
}

// Relevance-ranked SigLIP2 text-to-image retrieval. Kept separate from
// listImages/similarTo so callers cannot silently fuse text and image queries.
export function searchImagesSemantically(
  query: string,
  limit = 50,
  cursor?: string,
  signal?: AbortSignal,
): Promise<ImageListResponse> {
  const params = new URLSearchParams({ q: query, limit: String(limit) });
  if (cursor !== undefined && cursor.length > 0) params.set('cursor', cursor);
  return api<ImageListResponse>(`/api/images/semantic?${params.toString()}`, { signal });
}

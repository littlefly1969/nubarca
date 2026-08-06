import { api } from './client';

// Owner-private "similar photos" for an image (Photo Similarity). The DTO is
// already sanitized server-side: a logical FileItem id + name + a rounded score
// only — never raw vectors, BlobObjectId, SHA, StorageKey, or physical paths.
// Which embedding profile/model serves the result, and whether pgvector or the
// exact-scan fallback is used, are entirely server-side concerns and are NOT
// exposed here.
export interface SimilarPhotoItem {
  fileItemId: string;
  name: string;
  // Cosine similarity (0..1), rounded. The UI hides it from normal users.
  score: number;
  // ORIGINAL media pixel dimensions, from the metadata the server extracted at
  // ingestion — not a derivative's size and not measured at request time. They
  // exist so a result can be laid out at its true proportions instead of a
  // square guess. Null (both, always together) when the blob has no extracted
  // dimensions; callers must fall back rather than assume a ratio.
  width: number | null;
  height: number | null;
}

export interface SimilarPhotosResult {
  // The active image-embedding profile resolved + is usable.
  profileAvailable: boolean;
  // This photo has been indexed for the active profile.
  queryIndexed: boolean;
  items: SimilarPhotoItem[];
  // Sanitized reason token when profileAvailable is false (never shown to users).
  unavailableReason: string | null;
}

export function getSimilarPhotos(
  fileId: string,
  limit = 12,
  signal?: AbortSignal,
): Promise<SimilarPhotosResult> {
  return api<SimilarPhotosResult>(
    `/api/files/${encodeURIComponent(fileId)}/similar?limit=${limit}`,
    { signal },
  );
}

// Paginated, threshold-filtered variant for the Similar Photos Explorer. Same
// owner-private, sanitized DTO as above, plus a keyset cursor. `minSimilarity`
// is the user-facing threshold (0..1); results are ordered most-similar-first.
// The cursor is opaque (encodes only an already-exposed score + file id).
export interface SimilarPhotosPage {
  profileAvailable: boolean;
  queryIndexed: boolean;
  items: SimilarPhotoItem[];
  // Null at the end of the result set; pass back as `cursor` for the next page.
  nextCursor: string | null;
  hasMore: boolean;
  unavailableReason: string | null;
}

export interface SimilarPhotosPageQuery {
  minSimilarity: number;
  limit?: number;
  cursor?: string | null;
}

export function getSimilarPhotosPage(
  fileId: string,
  query: SimilarPhotosPageQuery,
  signal?: AbortSignal,
): Promise<SimilarPhotosPage> {
  const params = new URLSearchParams();
  params.set('minSimilarity', String(query.minSimilarity));
  if (query.limit !== undefined) params.set('limit', String(query.limit));
  if (query.cursor !== undefined && query.cursor !== null) {
    params.set('cursor', query.cursor);
  }
  return api<SimilarPhotosPage>(
    `/api/files/${encodeURIComponent(fileId)}/similar?${params.toString()}`,
    { signal },
  );
}

import { api } from './client';
import type { AlbumMembership, ImageSortDirection, ImageSortField, MediaGalleryScope } from './images';

// Slice 86: mirrors NubArca.Api.Files.VideoItem. `posterUrl` points at the
// existing GET /api/files/{id}/poster (generated on demand); playback uses
// GET /api/files/{id}/video (Range-enabled). No storage internals.
export interface VideoItem {
  id: string;
  // Logical file name — a title never renames the file (see ImageItem).
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
  posterUrl: string;
  durationSeconds: number | null;
  occurrenceCount: number;
  // Slice 95: poster provenance ("synthetic" | "ffmpeg" | ... | "unknown";
  // null when no poster row exists yet). Synthetic placeholders are marked in
  // the gallery so they aren't mistaken for real video frames.
  posterSource: string | null;
  // Six-frame JPEG sprite, loaded only after hover/focus.
  previewStripUrl: string | null;
  hasDuplicates: boolean;
  // ffprobe-derived, null until probed.
  videoCodec: string | null;
  audioCodec: string | null;
  hasAudio: boolean;
  frameRate: number | null;
}

// Mirrors VideoListResponse. Cursor pagination only (no offset).
export interface VideoListResponse {
  items: VideoItem[];
  limit: number;
  offset: number;
  count: number;
  nextCursor: string | null;
  hasMore: boolean;
}

export interface ListVideosQuery {
  q?: string;
  sort?: ImageSortField;
  direction?: ImageSortDirection;
  limit?: number;
  cursor?: string;
  folderId?: string;
  favorite?: boolean;
  minRating?: number;
  dateTakenFrom?: string;
  dateTakenTo?: string;
  collapseDuplicates?: boolean;
  // Video-metadata filters (ffprobe-derived).
  durationMin?: number;
  durationMax?: number;
  minWidth?: number;
  minHeight?: number;
  codec?: string;
  hasAudio?: boolean;
  // Same shared album-membership filter as the photo gallery.
  albumMembership?: AlbumMembership;
  // Slice 3: 'active' (default) vs 'excluded' ("Esclusi" tab). Part of the
  // cursor fingerprint, so switching scope resets pagination.
  mediaScope?: MediaGalleryScope;
}

export function listVideos(query: ListVideosQuery = {}, signal?: AbortSignal): Promise<VideoListResponse> {
  const params = new URLSearchParams();
  if (query.q) params.set('q', query.q);
  if (query.sort) params.set('sort', query.sort);
  if (query.direction) params.set('direction', query.direction);
  if (query.limit != null) params.set('limit', String(query.limit));
  if (query.cursor) params.set('cursor', query.cursor);
  if (query.folderId) params.set('folderId', query.folderId);
  if (query.favorite != null) params.set('favorite', String(query.favorite));
  if (query.minRating != null) params.set('minRating', String(query.minRating));
  if (query.dateTakenFrom) params.set('dateTakenFrom', query.dateTakenFrom);
  if (query.dateTakenTo) params.set('dateTakenTo', query.dateTakenTo);
  if (query.collapseDuplicates) params.set('collapseDuplicates', 'true');
  if (query.durationMin != null) params.set('durationMin', String(query.durationMin));
  if (query.durationMax != null) params.set('durationMax', String(query.durationMax));
  if (query.minWidth != null) params.set('minWidth', String(query.minWidth));
  if (query.minHeight != null) params.set('minHeight', String(query.minHeight));
  if (query.codec) params.set('codec', query.codec);
  if (query.hasAudio != null) params.set('hasAudio', String(query.hasAudio));
  if (query.albumMembership && query.albumMembership !== 'any') {
    params.set('albumMembership', query.albumMembership);
  }
  if (query.mediaScope && query.mediaScope !== 'active') {
    params.set('mediaScope', query.mediaScope);
  }
  const qs = params.toString();
  return api<VideoListResponse>(`/api/videos${qs ? `?${qs}` : ''}`, { signal });
}

// Distinct video codecs across the owner's videos — powers the codec filter.
export function listVideoCodecs(signal?: AbortSignal): Promise<{ codecs: string[] }> {
  return api<{ codecs: string[] }>(`/api/videos/codecs`, { signal });
}

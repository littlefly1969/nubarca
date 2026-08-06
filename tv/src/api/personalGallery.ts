// TV Personal Gallery API (grant-gated /api/tv/personal/... projection).
// Every call sends the limited TV session cookie (client.ts) AND the in-memory
// unlock grant header (personal.ts) — the server re-validates both on every
// request. DTOs mirror the backend TvPersonalGallery* records: display-safe
// fields + derived-media URLs only, never originals or storage internals.

import { ApiError, tvGet, tvPost, tvPut } from './client';
import { personalHeaders } from './personal';
import {
  buildGalleryQueryString,
  toCurrentFilterState,
  type GalleryFilters,
  type GallerySort,
  type InterpretResponse,
} from '../personal/galleryQuery';

export interface TvPersonalGalleryItem {
  id: string;
  name: string;
  mediaType: 'image';
  width: number | null;
  height: number | null;
  createdAt: string;
  thumbnailUrl: string;
  previewUrl: string;
  favorite: boolean;
  occurrenceCount: number;
}

export interface TvPersonalGalleryPage {
  items: TvPersonalGalleryItem[];
  nextCursor: string | null;
  hasMore: boolean;
  // Server-authoritative count of items matching the CURRENT filtered query
  // (not the number loaded so far). Stable across the pages of one query; the
  // viewer counter denominator. Present on every page (same value each time).
  // On a semantic query this is the REDUCED semantic result total (<= Top-K).
  totalCount: number;
  // Slice 100: present when a semantic query is applied.
  semanticActive?: boolean;
  semanticTopK?: number;
  semanticStatus?: 'ok' | 'unavailable' | 'indexing' | null;
}

export type InterpretErrorKind =
  | 'unsupported' | 'busy' | 'timeout' | 'unavailable' | 'failed' | 'auth';

export class InterpretError extends Error {
  constructor(public readonly kind: InterpretErrorKind) {
    super(kind);
    this.name = 'InterpretError';
  }
}

export interface TvPersonalPerson {
  id: string;
  name: string | null;
  faceCount: number;
}

export interface TvPersonalAlbum {
  id: string;
  name: string;
  itemCount: number;
}

export interface TvPersonalAlbumAddResult {
  requested: number;
  succeeded: number;
  skipped: number;
}

export interface TvPersonalGalleryBulkResult {
  requested: number;
  succeeded: number;
  skipped: number;
  succeededItemIds: string[];
  failures: { itemId: string; reason: string }[];
}

export interface TvPersonalMediaInfo {
  id: string;
  name: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  dateTaken: string;
  dateTakenSource: 'user' | 'embedded' | 'uploaded';
  cameraMake: string | null;
  cameraModel: string | null;
  lensModel: string | null;
  iso: number | null;
  aperture: number | null;
  exposureTime: string | null;
  focalLength: number | null;
  hasGps: boolean;
  title: string | null;
  description: string | null;
  tags: string[];
  rating: number | null;
  favorite: boolean;
  location: string | null;
}

export function listPersonalGallery(
  filters: GalleryFilters,
  sort: GallerySort,
  limit: number,
  cursor: string | null,
): Promise<TvPersonalGalleryPage> {
  const qs = buildGalleryQueryString(filters, sort, limit, cursor);
  return tvGet<TvPersonalGalleryPage>(`/api/tv/personal/gallery?${qs}`, personalHeaders());
}

export function listPersonalPeople(): Promise<TvPersonalPerson[]> {
  return tvGet<TvPersonalPerson[]>('/api/tv/personal/gallery/people', personalHeaders());
}

// LOCAL natural-language interpretation. Command in the POST body only; never a
// URL/log/storage. Maps HTTP status → a non-technical error kind.
export async function interpretCommand(
  command: string,
  filters: GalleryFilters,
  sort: GallerySort,
  lang: 'it' | 'en',
): Promise<InterpretResponse> {
  const request = {
    command,
    locale: lang === 'it' ? 'it-IT' : 'en-US',
    timeZone: 'Europe/Rome',
    currentDate: new Date().toISOString(),
    currentFilters: toCurrentFilterState(filters, sort),
  };
  try {
    return await tvPost<InterpretResponse>(
      '/api/tv/personal/gallery/interpret-command', request, personalHeaders());
  } catch (err) {
    const status = err instanceof ApiError ? err.status : 0;
    // Preserve the original auth error so the screen can apply the shared
    // session/grant invalidation path (including the pin_changed reason).
    if (status === 401 || status === 403) throw err;
    throw new InterpretError(mapStatus(status));
  }
}

function mapStatus(status: number): InterpretErrorKind {
  switch (status) {
    case 401: case 403: return 'auth';
    case 422: return 'unsupported';
    case 429: return 'busy';
    case 503: return 'unavailable';
    case 504: return 'timeout';
    default: return 'failed';
  }
}

export function listPersonalAlbums(): Promise<TvPersonalAlbum[]> {
  return tvGet<TvPersonalAlbum[]>('/api/tv/personal/gallery/albums', personalHeaders());
}

export function addPersonalItemsToAlbum(
  albumId: string,
  fileItemIds: string[],
): Promise<TvPersonalAlbumAddResult> {
  return tvPost<TvPersonalAlbumAddResult>(
    `/api/tv/personal/gallery/albums/${encodeURIComponent(albumId)}/items`,
    { fileItemIds },
    personalHeaders());
}

export type PersonalGalleryDestination = 'beauty-lab' | 'plates';

export function addPersonalItemsToDestination(
  destination: PersonalGalleryDestination,
  fileItemIds: string[],
): Promise<TvPersonalGalleryBulkResult> {
  return tvPost<TvPersonalGalleryBulkResult>(
    `/api/tv/personal/gallery/add-to-${destination}`,
    { fileItemIds },
    personalHeaders());
}

export function trashPersonalGalleryItems(
  fileItemIds: string[],
): Promise<TvPersonalGalleryBulkResult> {
  return tvPost<TvPersonalGalleryBulkResult>(
    '/api/tv/personal/gallery/trash',
    { fileItemIds },
    personalHeaders());
}

export function getPersonalMediaInfo(fileId: string): Promise<TvPersonalMediaInfo> {
  return tvGet<TvPersonalMediaInfo>(
    `/api/tv/personal/media/${encodeURIComponent(fileId)}/info`, personalHeaders());
}

export function setPersonalFavorite(
  fileId: string,
  favorite: boolean,
): Promise<{ id: string; favorite: boolean }> {
  return tvPut<{ id: string; favorite: boolean }>(
    `/api/tv/personal/media/${encodeURIComponent(fileId)}/favorite`,
    { favorite },
    personalHeaders());
}

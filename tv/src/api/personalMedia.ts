import { tvGet } from './client';
import { personalHeaders } from './personal';
import {
  queryToWire,
  type MediaWorkspaceIdentity,
} from '../personal/mediaWorkspaceQuery';

// Unified TV Personal media API.
//
// ONE endpoint family serves the library and every album, for all three kind
// tabs, because the server binds both through the SAME query binder the web
// workspace uses. There is deliberately no per-kind function here: a "list
// videos" call would be the first step back toward the two-surface split this
// replaced.

export interface TvPersonalMediaItem {
  id: string;
  kind: 'image' | 'video';
  displayName: string;
  width: number | null;
  height: number | null;
  createdAt: string;
  takenAt: string | null;
  favorite: boolean;
  rating: number | null;
  occurrenceCount: number;
  // Small thumbnail (photo) or poster (video) — the grid card image.
  cardImageUrl: string;
  // Medium preview (photo) or poster (video) — the viewer image.
  viewerImageUrl: string;
  // Video-only; null on photos.
  videoUrl: string | null;
  previewStripUrl: string | null;
  durationSeconds: number | null;
  videoCodec: string | null;
  hasAudio: boolean | null;
}

export interface TvPersonalMediaPage {
  items: TvPersonalMediaItem[];
  nextCursor: string | null;
  hasMore: boolean;
  totalCount: number;
  photoCount: number;
  videoCount: number;
}

export interface TvPersonalAlbumCard {
  id: string;
  name: string;
  itemCount: number;
  photoCount: number;
  videoCount: number;
  coverImageUrls: string[];
}

function toQueryString(wire: Record<string, string>): string {
  const parts = Object.entries(wire)
    .map(([key, value]) => `${encodeURIComponent(key)}=${encodeURIComponent(value)}`);
  return parts.length === 0 ? '' : `?${parts.join('&')}`;
}

// One page for the given identity. The path is chosen by the identity's SOURCE,
// and the parameters come from the shared `queryToWire` — so an album request
// cannot accidentally carry a library-only filter, and the two surfaces cannot
// drift into different parameter names.
export function listPersonalMedia(
  identity: MediaWorkspaceIdentity,
  cursor: string | null,
  limit?: number,
): Promise<TvPersonalMediaPage> {
  const qs = toQueryString(queryToWire(identity, cursor, limit));
  const path = identity.source.kind === 'album'
    ? `/api/tv/personal/albums/${encodeURIComponent(identity.source.albumId)}/media${qs}`
    : `/api/tv/personal/media${qs}`;
  return tvGet<TvPersonalMediaPage>(path, personalHeaders());
}

export function listPersonalAlbums(): Promise<TvPersonalAlbumCard[]> {
  return tvGet<TvPersonalAlbumCard[]>('/api/tv/personal/albums', personalHeaders());
}

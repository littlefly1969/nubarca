import { tvGet } from './client';
import { personalHeaders } from './personal';
import {
  queryToWire,
  semanticToWire,
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

/** Retrieval could not run. NOT an empty result — see searchPersonalMediaSemantic. */
export class SemanticUnavailableError extends Error {
  constructor(readonly reason: string | null) {
    super('semantic_unavailable');
    this.name = 'SemanticUnavailableError';
  }
}

/**
 * One page of SEMANTIC results for the current identity.
 *
 * A different canonical route from `listPersonalMedia` because it is a
 * different service with its own relevance cursor — but the SAME page DTO, so
 * the grid and the viewer cannot tell a semantic result from an ordinary one.
 *
 * There is deliberately no fallback here. If retrieval is unavailable this
 * throws, and the screen shows an explicit state: quietly returning metadata
 * matches would let the user believe they were seeing a semantic search when
 * they were seeing substring search.
 */
export async function searchPersonalMediaSemantic(
  identity: MediaWorkspaceIdentity,
  cursor: string | null,
  limit?: number,
): Promise<TvPersonalMediaPage> {
  const qs = toQueryString(semanticToWire(identity, cursor, limit));
  try {
    return await tvGet<TvPersonalMediaPage>(
      `/api/tv/personal/media/semantic${qs}`, personalHeaders());
  } catch (error) {
    const body = (error as { body?: { error?: string; reason?: string } } | null)?.body;
    if (body?.error === 'semantic_unavailable') {
      throw new SemanticUnavailableError(body.reason ?? null);
    }
    throw error;
  }
}

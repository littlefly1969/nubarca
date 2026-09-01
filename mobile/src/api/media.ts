// Mobile TRANSPORT for the unified media surface: GET /api/media and
// GET /api/albums/{albumId}/media.
//
// The DTOs, the query type and the query SERIALIZATION come from
// @nubarca/contracts — the same definitions the web and TV clients use — so
// the same semantic query produces the same request from every device. What
// remains here is the authenticated mobile transport, which legitimately
// differs and is duplication the architecture accepts.
//
// This file used to carry its own copy of MediaItem. That copy had drifted
// against the server in both directions: it was missing `favorite`, `rating`
// and `hasGps`, and it declared `audioCodec` and `frameRate`, which belong to
// VideoItem (/api/videos) and are never sent on this surface.

import { apiGet } from './client.ts';
import type {
  ListMediaQuery,
  MediaListResponse,
  SearchSemanticMediaQuery,
  SemanticMediaSearchResponse,
} from '@nubarca/contracts';
import {
  MEDIA_LIST_PATH,
  MEDIA_SEMANTIC_PATH,
  albumMediaPath,
  mediaQueryToParams,
  semanticMediaQueryToParams,
  withQuery,
} from '@nubarca/contracts';

export type {
  ImageMediaItem,
  SearchSemanticMediaQuery,
  SemanticMediaResultItem,
  SemanticMediaSearchResponse,
  ListMediaQuery,
  MediaItem,
  MediaKind,
  MediaListResponse,
  VideoMediaItem,
} from '@nubarca/contracts';

// The whole-library mixed grid and the Photos/Videos tabs via kind filtering —
// one backend concept, one wire vocabulary.
export function listMedia(
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  return apiGet<MediaListResponse>(
    withQuery(MEDIA_LIST_PATH, mediaQueryToParams(query)),
    signal,
  );
}

// Album detail's mixed-media grid: the same MediaItem projection, scoped to
// one album's membership.
export function listAlbumMedia(
  albumId: string,
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  return apiGet<MediaListResponse>(
    withQuery(albumMediaPath(albumId), mediaQueryToParams(query)),
    signal,
  );
}

/**
 * Visual/semantic search (§10).
 *
 * A conceptually DIFFERENT backend operation from physical filtering, and it
 * keeps its own route and its own query — one definition per operation, not one
 * endpoint for everything. Relevance ranking has its own cursor, which is why
 * the unified listing cannot simply carry a semantic term.
 */
export function searchSemanticMedia(
  query: SearchSemanticMediaQuery,
  signal?: AbortSignal,
): Promise<SemanticMediaSearchResponse> {
  return apiGet<SemanticMediaSearchResponse>(
    withQuery(MEDIA_SEMANTIC_PATH, semanticMediaQueryToParams(query)),
    signal,
  );
}

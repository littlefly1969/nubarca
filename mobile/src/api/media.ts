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
import type { ListMediaQuery, MediaListResponse } from '@nubarca/contracts';
import {
  MEDIA_LIST_PATH,
  albumMediaPath,
  mediaQueryToParams,
  withQuery,
} from '@nubarca/contracts';

export type {
  ImageMediaItem,
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

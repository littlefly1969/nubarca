// Web TRANSPORT for the unified media surface: GET /api/media,
// GET /api/albums/{albumId}/media and GET /api/media/semantic.
//
// The DTOs, the query types and — crucially — the query SERIALIZATION all come
// from @nubarca/contracts, so the phone and the television put exactly the
// same parameters on the wire as the browser does. Sharing an interface while
// each client kept its own parameter builder is precisely how two clients end
// up agreeing on a shape and disagreeing on a request.
//
// Everything is re-exported under its historical name so the existing web call
// sites keep importing from '@nubarca/api-client' unchanged. There is one
// definition; this is an alias of it, not a second copy.

import type {
  ListMediaQuery,
  MediaListResponse,
  SearchSemanticMediaQuery,
  SemanticMediaSearchResponse,
} from '@nubarca/contracts';
import {
  MEDIA_LIST_PATH,
  MEDIA_SEMANTIC_PATH,
  listMediaPath,
  albumMediaPath,
  mediaQueryToParams,
  mediaWindowQueryToParams,
  semanticMediaQueryToParams,
  withQuery,
} from '@nubarca/contracts';
import { api } from './client';

export type {
  AlbumMembership,
  ImageMediaItem,
  ListMediaQuery,
  MediaGalleryScope,
  MediaItem,
  MediaKind,
  MediaListResponse,
  MediaSortDirection,
  MediaSortField,
  PeopleMatchMode,
  SearchSemanticMediaQuery,
  SemanticBestMatch,
  SemanticMediaResultItem,
  SemanticMediaSearchResponse,
  VideoMediaItem,
} from '@nubarca/contracts';

export function listMedia(
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  return api<MediaListResponse>(
    withQuery(MEDIA_LIST_PATH, mediaQueryToParams(query)),
    { signal },
  );
}

export function listAlbumMedia(
  albumId: string,
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  return api<MediaListResponse>(
    withQuery(albumMediaPath(albumId), mediaQueryToParams(query)),
    { signal },
  );
}

// One owner media list (or the current selection) under the same contract.
export function listListMedia(
  listId: string,
  query: ListMediaQuery,
  signal?: AbortSignal,
): Promise<MediaListResponse> {
  return api<MediaListResponse>(
    withQuery(listMediaPath(listId), mediaQueryToParams(query)),
    { signal },
  );
}

// The collection a navigation index / window is read from: the library (null),
// an album (its id) or an owner list ({ listId }).
export type MediaNavigationSource = string | { listId: string } | null;

function navigationPath(source: MediaNavigationSource): string {
  if (source === null) return MEDIA_LIST_PATH;
  return typeof source === 'string' ? albumMediaPath(source) : listMediaPath(source.listId);
}

// VSEM-03: unified photo+video semantic search. A conceptually DIFFERENT
// backend operation from physical filtering — one relevance-ranked stream
// across both kinds, with bounded temporal evidence on videos — so it keeps
// its own route and its own query. No scores, vectors or internal identifiers
// are ever on the wire.
export function searchSemanticMedia(
  query: SearchSemanticMediaQuery,
  signal?: AbortSignal,
): Promise<SemanticMediaSearchResponse> {
  return api<SemanticMediaSearchResponse>(
    withQuery(MEDIA_SEMANTIC_PATH, semanticMediaQueryToParams(query)),
    { signal },
  );
}

export type { MediaNavigationBucket, MediaNavigationIndex, MediaNavigationWindow, MediaWindowQuery } from '@nubarca/contracts';

export function getMediaNavigation(
  source: MediaNavigationSource, query: ListMediaQuery, signal?: AbortSignal,
): Promise<import('@nubarca/contracts').MediaNavigationIndex> {
  const path = navigationPath(source);
  return api(withQuery(path + '/navigation', mediaQueryToParams(query)), { signal });
}

export function getMediaWindow(
  source: MediaNavigationSource, query: import('@nubarca/contracts').MediaWindowQuery, signal?: AbortSignal,
): Promise<import('@nubarca/contracts').MediaNavigationWindow> {
  const path = navigationPath(source);
  return api(withQuery(path + '/window', mediaWindowQueryToParams(query)), { signal });
}

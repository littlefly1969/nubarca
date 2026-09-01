// The page model the mobile grid actually consumes.
//
// It exists because the two sources answer different questions. The unified
// listing knows how many photos and videos matched; semantic ranking does not,
// and asking it would be asking for a number that has no meaning on that
// route. The previous shape forced the semantic path to invent
// `photoCount: 0, videoCount: 0` — data the server never sent, presented as
// though it had.
//
// So the shared shape carries only what BOTH sources really know. Anything one
// of them knows and the other does not is optional, and absent means absent.

import type { MediaItem, MediaListResponse, SemanticMediaSearchResponse } from '@nubarca/contracts';

export interface MediaPage {
  items: MediaItem[];
  nextCursor: string | null;
  hasMore: boolean;
  total: number;
  /** Per-kind counts, from the LISTING only. The semantic route does not
   * produce them, and absent is the honest answer there. */
  photoCount?: number;
  videoCount?: number;
}

export function pageFromListing(response: MediaListResponse): MediaPage {
  return {
    items: response.items,
    nextCursor: response.nextCursor,
    hasMore: response.hasMore,
    total: response.total,
    photoCount: response.photoCount,
    videoCount: response.videoCount,
  };
}

/**
 * A ranked page. The temporal evidence a video result carries is dropped here
 * rather than widened into the item type: this list surface does not show it,
 * and a field nothing renders is a field that drifts.
 */
export function pageFromSemantic(response: SemanticMediaSearchResponse): MediaPage {
  return {
    items: response.items.map((result) => result.media),
    nextCursor: response.nextCursor,
    hasMore: response.hasMore,
    total: response.total,
  };
}

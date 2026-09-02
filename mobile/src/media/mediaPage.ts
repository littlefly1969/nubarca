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

import type {
  MediaItem,
  MediaListResponse,
  SemanticBestMatch,
  SemanticMediaSearchResponse,
} from '@nubarca/contracts';

export interface MediaPage {
  items: MediaItem[];
  nextCursor: string | null;
  hasMore: boolean;
  total: number;
  /** Per-kind counts, from the LISTING only. The semantic route does not
   * produce them, and absent is the honest answer there. */
  photoCount?: number;
  videoCount?: number;
  /**
   * Where in a video the match actually is, by media id — the ranked route
   * only.
   *
   * This used to be dropped on the grounds that "this list surface does not
   * show it". That was wrong: a visual search that finds a video and cannot
   * say WHICH MOMENT matched has answered half the question, and on a device
   * it reads as a search that ignored the query. The projection now carries
   * it; what a surface does with it is the surface's decision, not the
   * mapper's.
   */
  evidence?: ReadonlyMap<string, SemanticBestMatch>;
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
 * A ranked page, carrying the temporal evidence alongside the items rather
 * than inside them: an item stays the server's MediaItem, and the "where in
 * the video" answer travels next to it.
 */
export function pageFromSemantic(response: SemanticMediaSearchResponse): MediaPage {
  return {
    items: response.items.map((result) => result.media),
    nextCursor: response.nextCursor,
    hasMore: response.hasMore,
    total: response.total,
    evidence: new Map(response.items.map((result) => [result.media.id, result.bestMatch])),
  };
}

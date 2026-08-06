import { useCallback } from 'react';
import { useNavigate } from 'react-router';

// The single source of truth for what the media viewer's details drawer offers
// on an open item, and where each action goes.
//
// The rule this file exists to enforce: the action set is a property of the
// ITEM, never of the surface the item was opened from. A photo opened from the
// Library, an album, the Similar Photos Explorer, a people view, a search result
// or a direct URL exposes exactly the same actions. Origin may decide HOW an
// action is carried out (the Library is already at the destination, so it
// filters in place instead of re-entering its own route) — never WHETHER the
// action is offered.
//
// Hosts must not re-derive eligibility. They call `useMediaSimilarityActions`
// and spread the result onto MediaMetadataPanel.

export type MediaViewerItemKind = 'image' | 'video';

export interface MediaViewerSubject {
  id: string;
  kind: MediaViewerItemKind;
}

// Gates that may legitimately suppress an action. Everything defaults to
// available, so a host that knows nothing extra passes nothing. This record is
// the ONLY place a new gate may be added — never an `if (route === …)` at a call
// site.
export interface MediaViewerCapabilities {
  // Photo similarity as a product capability (permissions / feature switch).
  // Undefined means "no host-supplied restriction", which is the normal case:
  // the endpoints are owner-scoped and report their own unavailability in the
  // destination view rather than by hiding the entry point.
  similarityAvailable?: boolean;
}

export interface MediaViewerSimilarityActionProps {
  onFindSimilarInLibrary?: () => void;
  onExploreSimilar?: () => void;
}

// ------------------------------------------------------------ canonical routes

export const MEDIA_LIBRARY_PATH = '/media';

/**
 * The Library, filtered by the canonical `similarTo` anchor on the photo tab.
 * This is the existing library-filter query model — the explorer's ranked
 * endpoint is a different feature and is NOT used here.
 */
export function findSimilarInLibraryPath(fileId: string): string {
  const sp = new URLSearchParams();
  sp.set('kind', 'image');
  sp.set('similarTo', fileId);
  return `${MEDIA_LIBRARY_PATH}?${sp.toString()}`;
}

/** The dedicated Similar Photos Explorer, rooted on `fileId`. */
export function exploreSimilarPath(fileId: string, minSimilarity?: number | null): string {
  const base = `/gallery/files/${fileId}/similar`;
  if (minSimilarity == null || !Number.isFinite(minSimilarity)) return base;
  return `${base}?minSimilarity=${minSimilarity.toFixed(2)}`;
}

// ---------------------------------------------------------------- eligibility

/**
 * Both similarity actions are photo-only: the anchor is a visual embedding of a
 * still image, so a video can never be one. Nothing else — and explicitly not
 * the current route — takes part in this decision.
 */
export function canUseSimilarityActions(
  subject: MediaViewerSubject,
  capabilities: MediaViewerCapabilities = {},
): boolean {
  return subject.kind === 'image' && capabilities.similarityAvailable !== false;
}

/**
 * Bind the eligible actions to a host's handlers. An ineligible subject yields
 * `undefined` for both, which is how MediaMetadataView drops the whole Discover
 * group.
 */
export function resolveMediaViewerSimilarityActions(
  subject: MediaViewerSubject,
  handlers: {
    findSimilarInLibrary(fileId: string): void;
    exploreSimilar(fileId: string): void;
  },
  capabilities: MediaViewerCapabilities = {},
): MediaViewerSimilarityActionProps {
  if (!canUseSimilarityActions(subject, capabilities)) return {};
  return {
    onFindSimilarInLibrary: () => handlers.findSimilarInLibrary(subject.id),
    onExploreSimilar: () => handlers.exploreSimilar(subject.id),
  };
}

// ---------------------------------------------------------------- host binding

export interface UseMediaSimilarityActionsOptions {
  /** Every host closes its viewer before the destination takes over. */
  onNavigate?: () => void;
  /**
   * The Library workspace only. It is already ON the destination route, and
   * `MediaLibraryPage` seeds its identity from the URL once at mount — so a
   * navigation to its own path would rewrite the query string without applying
   * the filter. It therefore sets the anchor on its live identity instead, which
   * also preserves the session-only filters that never reach the URL.
   */
  applyLibraryAnchor?: (fileId: string) => void;
  /** Explorer only: carry the reader's chosen threshold across a re-root. */
  exploreMinSimilarity?: number | null;
  /** Explorer only: the return target to keep offering after a re-root. */
  exploreState?: unknown;
  /**
   * Explorer only: the anchor currently being explored. Re-rooting onto it is
   * already satisfied, so it closes the viewer instead of pushing a duplicate
   * history entry onto itself.
   */
  currentExploreAnchor?: string | null;
  capabilities?: MediaViewerCapabilities;
}

/**
 * Returns a resolver that maps an open viewer item to the props
 * MediaMetadataPanel expects. Use it in every viewer host.
 */
export function useMediaSimilarityActions(
  options: UseMediaSimilarityActionsOptions = {},
): (subject: MediaViewerSubject) => MediaViewerSimilarityActionProps {
  const navigate = useNavigate();
  const {
    onNavigate,
    applyLibraryAnchor,
    exploreMinSimilarity = null,
    exploreState,
    currentExploreAnchor = null,
    capabilities,
  } = options;

  const findSimilarInLibrary = useCallback((fileId: string) => {
    onNavigate?.();
    if (applyLibraryAnchor) {
      applyLibraryAnchor(fileId);
      return;
    }
    void navigate(findSimilarInLibraryPath(fileId));
  }, [onNavigate, applyLibraryAnchor, navigate]);

  const exploreSimilar = useCallback((fileId: string) => {
    onNavigate?.();
    if (currentExploreAnchor === fileId) return;
    void navigate(
      exploreSimilarPath(fileId, exploreMinSimilarity),
      exploreState === undefined ? undefined : { state: exploreState },
    );
  }, [onNavigate, currentExploreAnchor, navigate, exploreMinSimilarity, exploreState]);

  return useCallback(
    (subject: MediaViewerSubject) => resolveMediaViewerSimilarityActions(
      subject,
      { findSimilarInLibrary, exploreSimilar },
      capabilities,
    ),
    [findSimilarInLibrary, exploreSimilar, capabilities],
  );
}

import { useEffect, useRef, useState } from 'react';
import { useAppScrollViewport } from '../../components/appScroll';

// Infinite scroll for a media wall, as one hook.
//
// Two things here are easy to get wrong, and both were paid for once already.
//
// The observer's root is the APPLICATION scroll viewport, not the browser
// viewport. `.app-main` owns the scrolling and clips what overflows it, and a
// root's margin inflates only the root — an intermediate clip is applied
// unexpanded. Left document-rooted, the preload margin would be swallowed by
// that clip and the next page would not start loading until the sentinel was
// already on screen, which is the stall the margin exists to prevent. Outside
// the shell there is no viewport and `null` keeps document-rooted behaviour.
//
// And an IntersectionObserver only fires on a TRANSITION. When a page settles
// with the sentinel still inside the (large) preload margin, no further callback
// comes, so the chain stalls at the bottom until the user scrolls up and back
// down. The effect below keeps loading while the sentinel stays visible.

const PRELOAD_MARGIN = '1400px 0px';

export interface WallSentinelInput {
  // The wall is idle and could accept another page (not already loading one).
  ready: boolean;
  hasMore: boolean;
  loadMore(): void;
  preloadMargin?: string;
  // For prepend/jump: require a fresh observation after this geometry changes.
  // Unlike bottom paging, the old intersection cannot authorize another read.
  reobserveKey?: unknown;
}

/** Attach the returned setter to the sentinel element below the wall. */
export function useWallSentinel({ ready, hasMore, loadMore, preloadMargin = PRELOAD_MARGIN, reobserveKey }: WallSentinelInput) {
  const viewportRef = useAppScrollViewport();
  const visibleRef = useRef(false);
  const loadMoreRef = useRef(loadMore);
  loadMoreRef.current = loadMore;
  const freshIntersection = reobserveKey !== undefined;
  const observeReady = freshIntersection ? ready && hasMore : true;
  const currentObservation = useRef({ ready, hasMore, reobserveKey });
  currentObservation.current = { ready, hasMore, reobserveKey };

  // The sentinel node as state, not a callback ref: the observer is then created
  // from an effect, which runs after every ref in the commit is attached, so the
  // application scroll viewport is never read too early.
  const [node, setNode] = useState<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!node || !observeReady || typeof IntersectionObserver === 'undefined') return;
    let active = true;
    const observer = new IntersectionObserver(
      (entries) => {
        if (!active) return;
        // A callback can already be queued when React commits new geometry,
        // before passive cleanup disconnects the old observer.
        const current = currentObservation.current;
        if (freshIntersection && (!current.ready || !current.hasMore || current.reobserveKey !== reobserveKey)) return;
        visibleRef.current = entries.some((e) => e.isIntersecting);
        if (visibleRef.current) loadMoreRef.current();
      },
      { root: viewportRef?.current ?? null, rootMargin: preloadMargin },
    );
    observer.observe(node);
    return () => {
      active = false;
      observer.disconnect();
      // The sentinel is gone (a new query, or the end of the set): its last
      // known visibility must not seed the chaining effect for a different
      // result.
      visibleRef.current = false;
    };
    // A new observer reports its initial intersection even if it remains
    // inside the margin. Effects run after the wall's scroll-anchor layout
    // effect, so this evaluation includes the compensated scroll position.
  }, [node, viewportRef, preloadMargin, reobserveKey, observeReady]);

  useEffect(() => {
    if (!freshIntersection && ready && hasMore && visibleRef.current) loadMoreRef.current();
  }, [ready, hasMore, freshIntersection]);

  return setNode;
}

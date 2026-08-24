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
}

/** Attach the returned setter to the sentinel element below the wall. */
export function useWallSentinel({ ready, hasMore, loadMore }: WallSentinelInput) {
  const viewportRef = useAppScrollViewport();
  const visibleRef = useRef(false);
  const loadMoreRef = useRef(loadMore);
  loadMoreRef.current = loadMore;

  // The sentinel node as state, not a callback ref: the observer is then created
  // from an effect, which runs after every ref in the commit is attached, so the
  // application scroll viewport is never read too early.
  const [node, setNode] = useState<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!node || typeof IntersectionObserver === 'undefined') return;
    const observer = new IntersectionObserver(
      (entries) => {
        visibleRef.current = entries.some((e) => e.isIntersecting);
        if (visibleRef.current) loadMoreRef.current();
      },
      { root: viewportRef?.current ?? null, rootMargin: PRELOAD_MARGIN },
    );
    observer.observe(node);
    return () => {
      observer.disconnect();
      // The sentinel is gone (a new query, or the end of the set): its last
      // known visibility must not seed the chaining effect for a different
      // result.
      visibleRef.current = false;
    };
  }, [node, viewportRef]);

  useEffect(() => {
    if (ready && hasMore && visibleRef.current) loadMoreRef.current();
  }, [ready, hasMore]);

  return setNode;
}

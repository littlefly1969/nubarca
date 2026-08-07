import { createContext, useContext, type ReactNode, type RefObject } from 'react';

// The authenticated shell's scroll viewport.
//
// The application shell owns the browser viewport: the top bar and the sidebar
// are rows of a full-height layout and `.app-main` is the only box that scrolls.
// That is a layout decision, but it changes an API: anything that needs to know
// WHERE scrolling happens — an IntersectionObserver's root, a virtualizer's
// scroll element, a "start this new result at the top" reset — can no longer
// assume `window`/`document`, because they never move.
//
// The value is a stable ref object rather than the element itself. React attaches
// child refs before ancestor refs, so an element handed down through state would
// be null during the first commit of every page below the shell. A ref is read
// from an effect (or from a virtualizer's getScrollElement callback), by which
// time the whole commit — `<main>` included — has its refs.
//
// Outside the shell (login, TV pairing, the public party surfaces, unit tests)
// there is no provider and the hook returns null, which every consumer reads as
// "the document scrolls, exactly as it always did".

const AppScrollContext = createContext<RefObject<HTMLElement | null> | null>(null);

export function AppScrollProvider({
  viewportRef,
  children,
}: {
  viewportRef: RefObject<HTMLElement | null>;
  children: ReactNode;
}) {
  return <AppScrollContext.Provider value={viewportRef}>{children}</AppScrollContext.Provider>;
}

/**
 * The application scroll viewport, or null when the document is the scroll owner.
 *
 * Stable for the lifetime of a mount, so a consumer may branch on whether it is
 * present (which scroll model applies) without that branch ever flipping.
 */
export function useAppScrollViewport(): RefObject<HTMLElement | null> | null {
  return useContext(AppScrollContext);
}

/**
 * Send the application scroll viewport back to the top.
 *
 * Falls back to the document's scrolling element so callers do not have to carry
 * two code paths for surfaces rendered outside the shell.
 */
export function scrollViewportToTop(viewportRef: RefObject<HTMLElement | null> | null): void {
  const target = viewportRef?.current ?? document.scrollingElement;
  if (target) target.scrollTop = 0;
}

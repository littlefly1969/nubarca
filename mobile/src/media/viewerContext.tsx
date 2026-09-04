// Viewer context: carries the media sequence into the /media/[id] route.
//
// The grid owns the loaded page; opening the viewer hands the WHOLE visible
// sequence to this in-memory context so the viewer can swipe within it.
//
// PRIVACY CONTRACT (acceptance BLOCKER): viewer state is scoped to the
// signed-in identity. The provider is MOUNTED PER IDENTITY — app/_layout.tsx
// keys it on viewerIdentityKey(session) — so an account switch or a sign-out
// remounts it with a brand-new model: the FIRST render under a new identity
// observes an empty sequence BY CONSTRUCTION, not after some effect fires.
// There is deliberately no "last sequence kept for the animation": that ref
// was what once let state survive an account switch.

import { forgetAllPositions } from './videoPosition';
import React, {
  createContext,
  useCallback,
  useContext,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  ViewerSequenceModel,
  type ViewerReturnPosition,
  type ViewerSlide,
  type ViewerSequenceSnapshot,
} from './viewerSequence';

/** What the originating gallery hands back when asked to continue. */
export interface ViewerContinuationResult {
  slides: ViewerSlide[];
  hasMore: boolean;
}

/**
 * How the viewer asks the gallery that opened it for more.
 *
 * This is the ENTIRE contract. The viewer never learns a cursor, a page size,
 * an endpoint or a filter: there is one paginator, and it stays in the gallery.
 */
export interface ViewerContinuation {
  hasMore: boolean;
  loadMore: () => Promise<ViewerContinuationResult>;
}

interface ViewerContextValue {
  sequence: ViewerSequenceSnapshot | null;
  /** `scopeKey` names the gallery: photos, videos, album:<id>, shared-album:<id>. */
  open: (
    slides: ViewerSlide[],
    focusedKey: string,
    scopeKey: string,
    continuation?: ViewerContinuation,
  ) => void;
  /**
   * Ask the originating gallery to load its next page. Safe to call often: it
   * suppresses duplicates and stops asking once there is nothing left.
   */
  requestMore: () => Promise<void>;
  setIndex: (index: number) => void;
  close: () => void;
  /** Consumed only by the gallery that opened the viewer. */
  takeReturnPosition: (scopeKey: string) => ViewerReturnPosition | null;
}

const ViewerContext = createContext<ViewerContextValue | null>(null);

export function ViewerProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  // NOTE: deliberately NOT reading the session here — identity scoping happens
  // by REMOUNTING this component (see the key in app/_layout.tsx), so no code
  // path can render a snapshot that belongs to a previous identity.
  // Remembered video positions live outside React so they can survive a slide
  // REMOUNT (see media/videoPosition.ts). That means they must be cleared on
  // the same boundary this provider already enforces: it is remounted whenever
  // the signed-in identity changes, so clearing on mount guarantees nothing a
  // previous account watched can be seen by the next one.
  const clearedRef = useRef(false);
  if (!clearedRef.current) {
    clearedRef.current = true;
    forgetAllPositions();
  }
  const modelRef = useRef<ViewerSequenceModel>(new ViewerSequenceModel());
  // The continuation is transient PROVIDER behaviour, deliberately not part of
  // the snapshot: the snapshot is renderable state, and a function is not.
  const continuationRef = useRef<ViewerContinuation | null>(null);
  const inFlightRef = useRef(false);
  // Bumped by every open and every close. A result carrying an older token
  // belongs to a sequence the user has already left — a different album, or a
  // different account — and is dropped rather than appended.
  const generationRef = useRef(0);
  const [snapshot, setSnapshotState] = useState<ViewerSequenceSnapshot | null>(
    () => modelRef.current.snapshot(),
  );

  const sync = useCallback(() => {
    setSnapshotState(modelRef.current.snapshot());
  }, []);

  const open = useCallback(
    (
      slides: ViewerSlide[],
      focusedKey: string,
      scopeKey: string,
      continuation?: ViewerContinuation,
    ) => {
      generationRef.current += 1;
      continuationRef.current = continuation ?? null;
      inFlightRef.current = false;
      modelRef.current.open(slides, focusedKey, scopeKey);
      sync();
    },
    [sync],
  );

  const requestMore = useCallback(async () => {
    const continuation = continuationRef.current;
    if (continuation === null) return;
    // Nothing left, or someone already asked. Both are ordinary: the route
    // calls this on every index change near the end.
    if (!continuation.hasMore) return;
    if (inFlightRef.current) return;
    const generation = generationRef.current;
    inFlightRef.current = true;
    try {
      const result = await continuation.loadMore();
      // Closed, reopened, or the account changed while we waited.
      if (generation !== generationRef.current) return;
      // hasMore first: it must be updated even when the page brought nothing
      // new, or the viewer would keep asking for a page that will never come.
      continuationRef.current = { ...continuation, hasMore: result.hasMore };
      if (modelRef.current.appendSlides(result.slides)) sync();
    } catch {
      // A failed page leaves everything usable: the slides already loaded, the
      // current item, and hasMore, so a later swipe can ask again. There is
      // deliberately no viewer-owned retry — the gallery's PagedList already
      // holds the cursor for one.
    } finally {
      if (generation === generationRef.current) inFlightRef.current = false;
    }
  }, [sync]);

  const setIndex = useCallback(
    (index: number) => {
      modelRef.current.setIndex(index);
      sync();
    },
    [sync],
  );

  const close = useCallback(() => {
    // Invalidate before dropping the sequence, so a page still in flight cannot
    // append to the one being closed. The gallery's own request is left alone:
    // if it completes it keeps the page, which is exactly what the user wants
    // waiting for them when they land back on the grid.
    generationRef.current += 1;
    continuationRef.current = null;
    inFlightRef.current = false;
    modelRef.current.close();
    sync();
  }, [sync]);

  const takeReturnPosition = useCallback(
    (scopeKey: string) => modelRef.current.takeReturnPosition(scopeKey),
    [],
  );

  const value = useMemo<ViewerContextValue>(
    () => ({ sequence: snapshot, open, requestMore, setIndex, close, takeReturnPosition }),
    [snapshot, open, requestMore, setIndex, close, takeReturnPosition],
  );

  return <ViewerContext.Provider value={value}>{children}</ViewerContext.Provider>;
}

export function useViewer(): ViewerContextValue {
  const ctx = useContext(ViewerContext);
  if (ctx === null) throw new Error('useViewer must be used within ViewerProvider');
  return ctx;
}


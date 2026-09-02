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
import { ViewerSequenceModel, type ViewerSlide, type ViewerSequenceSnapshot } from './viewerSequence';

interface ViewerContextValue {
  sequence: ViewerSequenceSnapshot | null;
  open: (slides: ViewerSlide[], focusedKey: string) => void;
  setIndex: (index: number) => void;
  close: () => void;
  /** The item a returning gallery should scroll to. Consumed on read. */
  takeReturnAnchor: () => string | null;
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
  const [snapshot, setSnapshotState] = useState<ViewerSequenceSnapshot | null>(
    () => modelRef.current.snapshot(),
  );

  const sync = useCallback(() => {
    setSnapshotState(modelRef.current.snapshot());
  }, []);

  const open = useCallback(
    (slides: ViewerSlide[], focusedKey: string) => {
      modelRef.current.open(slides, focusedKey);
      sync();
    },
    [sync],
  );

  const setIndex = useCallback(
    (index: number) => {
      modelRef.current.setIndex(index);
      sync();
    },
    [sync],
  );

  const close = useCallback(() => {
    modelRef.current.close();
    sync();
  }, [sync]);

  const takeReturnAnchor = useCallback(() => modelRef.current.takeReturnAnchor(), []);

  const value = useMemo<ViewerContextValue>(
    () => ({ sequence: snapshot, open, setIndex, close, takeReturnAnchor }),
    [snapshot, open, setIndex, close, takeReturnAnchor],
  );

  return <ViewerContext.Provider value={value}>{children}</ViewerContext.Provider>;
}

export function useViewer(): ViewerContextValue {
  const ctx = useContext(ViewerContext);
  if (ctx === null) throw new Error('useViewer must be used within ViewerProvider');
  return ctx;
}


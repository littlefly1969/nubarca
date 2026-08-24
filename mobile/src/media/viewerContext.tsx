// Viewer context: carries the media sequence into the /media/[id] route.
//
// The grid owns the loaded page; opening the viewer hands the WHOLE visible
// sequence to this in-memory context so the viewer can swipe within it.
//
// PRIVACY CONTRACT (acceptance BLOCKER): viewer state is scoped to the
// signed-in identity. The provider watches the session — an account switch or
// a sign-out RESETS the model completely, dropping every slide and every
// reference, so no sequence or metadata from user A can ever be observed by
// user B. There is deliberately no "last sequence kept for the animation":
// that ref was what once let state survive an account switch.

import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import { ViewerSequenceModel, type ViewerSlide, type ViewerSequenceSnapshot } from './viewerSequence';
import { useSession } from '../session/SessionProvider';

interface ViewerContextValue {
  sequence: ViewerSequenceSnapshot | null;
  open: (slides: ViewerSlide[], focusedKey: string) => void;
  setIndex: (index: number) => void;
  close: () => void;
}

const ViewerContext = createContext<ViewerContextValue | null>(null);

export function ViewerProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const session = useSession();
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

  // Identity watch: whenever the signed-in identity changes — including to
  // and from anonymous — the whole sequence is wiped before anything else can
  // observe it. First mount is skipped (nothing to wipe on cold start).
  const identityRef = useRef<string | null>(null);
  useEffect(() => {
    const identity =
      session.status === 'authed' ? `user:${session.user?.id ?? '?'}` : 'anonymous';
    if (identityRef.current !== null && identityRef.current !== identity) {
      modelRef.current.reset();
      setSnapshotState(modelRef.current.snapshot());
    }
    identityRef.current = identity;
  }, [session.status, session.user?.id]);

  const value = useMemo<ViewerContextValue>(
    () => ({ sequence: snapshot, open, setIndex, close }),
    [snapshot, open, setIndex, close],
  );

  return <ViewerContext.Provider value={value}>{children}</ViewerContext.Provider>;
}

export function useViewer(): ViewerContextValue {
  const ctx = useContext(ViewerContext);
  if (ctx === null) throw new Error('useViewer must be used within ViewerProvider');
  return ctx;
}


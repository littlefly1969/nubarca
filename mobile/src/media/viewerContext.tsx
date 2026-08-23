// Viewer context: carries the media sequence into the /media/[id] route.
//
// The grid owns the loaded page; opening the viewer hands the WHOLE visible
// sequence (plus the focused id) to this in-memory context so the viewer can
// swipe within it. Route params stay tiny; nothing is re-fetched to rebuild a
// sequence the screen already has.

import React, { createContext, useCallback, useContext, useMemo, useRef, useState } from 'react';
import type { MediaItem } from '../api/media';

export interface ViewerSequence {
  items: MediaItem[];
  focusedId: string | null;
}

interface ViewerContextValue {
  sequence: ViewerSequence | null;
  open: (items: MediaItem[], focusedId: string) => void;
  close: () => void;
}

const ViewerContext = createContext<ViewerContextValue | null>(null);

export function ViewerProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const [sequence, setSequence] = useState<ViewerSequence | null>(null);
  // Keep the LAST sequence mounted during the close transition so the modal
  // does not flash empty while animating out.
  const lastRef = useRef<ViewerSequence | null>(null);
  if (sequence !== null) lastRef.current = sequence;

  const open = useCallback((items: MediaItem[], focusedId: string) => {
    setSequence({ items, focusedId });
  }, []);

  const close = useCallback(() => {
    setSequence(null);
  }, []);

  const value = useMemo<ViewerContextValue>(
    () => ({ sequence: sequence ?? lastRef.current, open, close }),
    [sequence, open, close],
  );

  return <ViewerContext.Provider value={value}>{children}</ViewerContext.Provider>;
}

export function useViewer(): ViewerContextValue {
  const ctx = useContext(ViewerContext);
  if (ctx === null) throw new Error('useViewer must be used within ViewerProvider');
  return ctx;
}

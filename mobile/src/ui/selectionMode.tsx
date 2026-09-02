// Whether a gallery is currently in selection mode.
//
// The tab bar and the selection tray are both overlays anchored to the bottom
// of the screen, so they can occupy the same space — and did, with the tray
// rendered underneath the translucent navigation and its actions unreachable.
//
// The fix is STATE, not z-index. Selection mode is published here, the tab bar
// steps aside while it is on, and the tray takes its place. Only one bottom
// surface exists at a time, which is a property of the render rather than of a
// stacking order somebody has to keep true.
//
// Navigation state itself stays where it belongs: the tab navigator remains the
// authority for which destination is current, and hiding its bar changes
// nothing about that.

import React, { createContext, useContext, useEffect, useMemo, useState } from 'react';

interface SelectionModeValue {
  selecting: boolean;
  setSelecting: (selecting: boolean) => void;
}

const SelectionModeContext = createContext<SelectionModeValue | null>(null);

export function SelectionModeProvider({
  children,
}: {
  children: React.ReactNode;
}): React.JSX.Element {
  const [selecting, setSelecting] = useState(false);
  const value = useMemo(() => ({ selecting, setSelecting }), [selecting]);
  return (
    <SelectionModeContext.Provider value={value}>{children}</SelectionModeContext.Provider>
  );
}

/** Read-only, for the chrome that has to step aside. */
export function useSelectionMode(): boolean {
  return useContext(SelectionModeContext)?.selecting ?? false;
}

/**
 * Publish a gallery's selection state.
 *
 * Clears on unmount as well as on change: leaving a screen mid-selection must
 * not leave the navigation hidden behind a mode nobody is in any more.
 */
export function useReportSelectionMode(selecting: boolean): void {
  const context = useContext(SelectionModeContext);
  useEffect(() => {
    context?.setSelecting(selecting);
    return () => context?.setSelecting(false);
  }, [context, selecting]);
}

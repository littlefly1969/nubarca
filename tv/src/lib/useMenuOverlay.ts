import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

// The controls/QR overlay auto-hides after this idle window (requirement: ~6s).
export const OVERLAY_IDLE_MS = 6000;

// Shared controller for the remote-MENU overlay used by BOTH the album grid and
// the slideshow, so the interaction model stays consistent:
//
//  - hidden by default (no chrome, no QR);
//  - the MENU button toggles it (the caller wires `toggle` to eventType 'menu');
//  - it auto-hides after OVERLAY_IDLE_MS of inactivity — any remote activity
//    while visible should call `bump` to re-arm the timer;
//  - hardware Back should call `hide` first (the caller checks `visibleRef`).
//
// `visibleRef` mirrors `visible` so the (identity-stable) TV-event and Back
// callbacks always read the latest value without re-subscribing.
export function useMenuOverlay(): {
  visible: boolean;
  visibleRef: RefObject<boolean>;
  show: () => void;
  hide: () => void;
  toggle: () => void;
  bump: () => void;
} {
  const [visible, setVisible] = useState(false);
  const visibleRef = useRef(false);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  useEffect(() => { visibleRef.current = visible; }, [visible]);

  const clearTimer = useCallback(() => {
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
  }, []);

  const schedule = useCallback(() => {
    clearTimer();
    timer.current = setTimeout(() => setVisible(false), OVERLAY_IDLE_MS);
  }, [clearTimer]);

  const show = useCallback(() => {
    setVisible(true);
    schedule();
  }, [schedule]);

  const hide = useCallback(() => {
    setVisible(false);
    clearTimer();
  }, [clearTimer]);

  const toggle = useCallback(() => {
    if (visibleRef.current) hide();
    else show();
  }, [hide, show]);

  // Re-arm the auto-hide window on remote/focus activity while visible.
  const bump = useCallback(() => {
    if (visibleRef.current) schedule();
  }, [schedule]);

  useEffect(() => () => clearTimer(), [clearTimer]);

  return { visible, visibleRef, show, hide, toggle, bump };
}

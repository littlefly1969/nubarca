import { useCallback, useEffect, useRef, useState, type RefObject } from 'react';

// The viewer's MENU overlay — the app's useMenuOverlay, in a browser. MENU (or
// a pointer that moves) shows it, it hides itself after a quiet spell, and BACK
// hides it before doing anything else.

export const OVERLAY_IDLE_MS = 10_000;

export function useMenuOverlay(initiallyVisible = false): {
  visible: boolean;
  visibleRef: RefObject<boolean>;
  show: () => void;
  hide: () => void;
  toggle: () => void;
  bump: () => void;
} {
  const [visible, setVisible] = useState(initiallyVisible);
  const visibleRef = useRef(initiallyVisible);
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
    visibleRef.current = true;
    setVisible(true);
    schedule();
  }, [schedule]);
  const hide = useCallback(() => {
    visibleRef.current = false;
    setVisible(false);
    clearTimer();
  }, [clearTimer]);
  const toggle = useCallback(() => {
    if (visibleRef.current) hide();
    else show();
  }, [hide, show]);
  const bump = useCallback(() => {
    if (visibleRef.current) schedule();
  }, [schedule]);

  useEffect(() => {
    if (initiallyVisible) schedule();
    return () => clearTimer();
  }, [initiallyVisible, schedule, clearTimer]);

  return { visible, visibleRef, show, hide, toggle, bump };
}

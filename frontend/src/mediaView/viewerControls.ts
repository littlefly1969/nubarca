import { useCallback, useEffect, useRef, useState, type RefObject, type TouchEvent } from 'react';
import { isEditableKeyboardTarget, ownsKeyboardEvent } from '../components/keyboardOwnership';

// The controls every full-screen viewer shares, the owner's and the public one:
// chrome that steps aside while a picture is being looked at, the keyboard, and
// a swipe between neighbours. What the chrome CONTAINS is each viewer's own.

/** How long the controls stay after the last sign of a hand. */
export const IDLE_HIDE_MS = 2600;

/**
 * Controls that disappear when nobody is touching anything, and come back on
 * the next touch, move or key. Shown afresh whenever `resetKey` changes — a new
 * picture is a moment the controls are wanted.
 */
export function useIdleChrome(resetKey: unknown) {
  const [visible, setVisible] = useState(true);
  const visibleRef = useRef(true);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const clear = () => {
    if (timer.current) clearTimeout(timer.current);
    timer.current = null;
  };
  const show = useCallback(() => {
    clear();
    visibleRef.current = true;
    setVisible(true);
    timer.current = setTimeout(() => {
      visibleRef.current = false;
      setVisible(false);
    }, IDLE_HIDE_MS);
  }, []);
  const toggle = useCallback(() => {
    if (!visibleRef.current) {
      show();
      return;
    }
    clear();
    visibleRef.current = false;
    setVisible(false);
  }, [show]);

  useEffect(() => {
    show();
    return clear;
  }, [show, resetKey]);

  return { visible, show, toggle };
}

/**
 * Escape closes, arrows move — but only when this viewer is the surface the key
 * belongs to (keyboardOwnership): a modal opened on top owns its own keys, and
 * a caret in a field is not navigation.
 */
export function useViewerKeys(
  rootRef: RefObject<HTMLElement | null>,
  { onClose, onPrevious, onNext }: { onClose(): void; onPrevious?: () => void; onNext?: () => void },
) {
  useEffect(() => {
    function onKey(e: KeyboardEvent) {
      if (!ownsKeyboardEvent(rootRef.current, e.target)) return;
      if (e.key === 'Escape') { onClose(); return; }
      if (isEditableKeyboardTarget(e.target)) return;
      if (e.key === 'ArrowLeft') onPrevious?.();
      else if (e.key === 'ArrowRight') onNext?.();
    }
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [rootRef, onClose, onPrevious, onNext]);
}

/**
 * One finger flicked sideways moves to a neighbour. Not while the picture is
 * zoomed — a finger on an enlarged photograph is panning it — and never for a
 * gesture that started with two.
 */
export function useSwipe(
  { onPrevious, onNext, disabled = false }: { onPrevious?: () => void; onNext?: () => void; disabled?: boolean },
) {
  const startX = useRef<number | null>(null);
  const onTouchStart = useCallback((e: TouchEvent) => {
    startX.current = e.touches.length === 1 && !disabled ? e.touches[0]?.clientX ?? null : null;
  }, [disabled]);
  const onTouchEnd = useCallback((e: TouchEvent) => {
    const start = startX.current;
    startX.current = null;
    if (start == null) return;
    const dx = (e.changedTouches[0]?.clientX ?? start) - start;
    if (Math.abs(dx) > 50) { if (dx > 0) onPrevious?.(); else onNext?.(); }
  }, [onPrevious, onNext]);
  return { onTouchStart, onTouchEnd };
}

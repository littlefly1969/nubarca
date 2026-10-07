import { useEffect, useRef, type PointerEvent } from 'react';

// Let the browser own panning. Capture only keeps pointer movement observable;
// scroll/pointercancel invalidate the hold before it can become a selection.
export function useMediaLongPress(onSelect: () => void, enabled: boolean) {
  const latest = useRef(onSelect);
  latest.current = onSelect;
  const gesture = useRef<{ id: number; x: number; y: number } | null>(null);
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const suppressClick = useRef(false);
  const stopWatching = useRef<(() => void) | null>(null);

  function cancel() {
    if (timer.current !== null) clearTimeout(timer.current);
    timer.current = null;
    gesture.current = null;
    stopWatching.current?.();
    stopWatching.current = null;
  }

  useEffect(() => cancel, []);
  useEffect(() => { if (!enabled) cancel(); }, [enabled]);

  return {
    onPointerDown(event: PointerEvent<HTMLButtonElement>) {
      cancel();
      suppressClick.current = false;
      if (!enabled || !event.isPrimary || event.button !== 0 || !['touch', 'pen'].includes(event.pointerType)) return;
      gesture.current = { id: event.pointerId, x: event.clientX, y: event.clientY };
      event.currentTarget.setPointerCapture?.(event.pointerId);
      window.addEventListener('scroll', cancel, { capture: true, passive: true });
      window.addEventListener('blur', cancel);
      stopWatching.current = () => {
        window.removeEventListener('scroll', cancel, true);
        window.removeEventListener('blur', cancel);
      };
      timer.current = setTimeout(() => {
        timer.current = null;
        suppressClick.current = true;
        latest.current();
        navigator.vibrate?.(15);
      }, 480);
    },
    onPointerMove(event: PointerEvent<HTMLButtonElement>) {
      const held = gesture.current;
      if (held?.id === event.pointerId && Math.hypot(event.clientX - held.x, event.clientY - held.y) > 10) cancel();
    },
    onPointerUp: cancel,
    onPointerCancel: cancel,
    onLostPointerCapture: cancel,
    consumeClick() {
      const blocked = suppressClick.current;
      suppressClick.current = false;
      return blocked;
    },
    blocksContextMenu: () => gesture.current !== null || suppressClick.current,
  };
}

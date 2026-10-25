import { useRef, type KeyboardEvent, type PointerEvent } from 'react';

interface MailSplitterProps {
  // "vertical": a vertical bar between two columns, dragged sideways.
  // "horizontal": a horizontal bar between two rows, dragged up and down.
  orientation: 'vertical' | 'horizontal';
  // Size in px of the pane BEFORE the bar (left or above), which it controls.
  value: number;
  min: number;
  max: number;
  // What a double click (or Enter) puts back.
  defaultValue: number;
  onChange: (px: number) => void;
  // Told when a drag starts and ends, so the workspace can stop text selection
  // and keep the message frame from swallowing the pointer meanwhile.
  onDragChange?: (dragging: boolean) => void;
  label: string;
  // The id of the pane it sizes.
  controls: string;
  testId?: string;
}

const STEP = 16;
const BIG_STEP = 64;

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), Math.max(min, max));
}

// A window-splitter (WAI-ARIA "separator", focusable): drag it with a mouse,
// pen or finger, or focus it and use the arrow keys (Shift for bigger steps),
// Home/End for the limits, Enter or a double click for the default.
//
// The drag holds pointer capture, so the release is seen wherever it happens —
// over the message frame, outside the bar, outside the window — and a lost
// capture ends the drag rather than leaving it stuck. The value is clamped on
// every move, so the panes never pass their minimums.
export function MailSplitter({
  orientation, value, min, max, defaultValue, onChange, onDragChange, label, controls, testId,
}: MailSplitterProps) {
  const drag = useRef<{ pointerId: number; origin: number; start: number; last: number } | null>(null);
  const vertical = orientation === 'vertical';
  const now = Math.round(clamp(value, min, max));

  function coordinate(e: PointerEvent<HTMLDivElement>) {
    return vertical ? e.clientX : e.clientY;
  }

  function onPointerDown(e: PointerEvent<HTMLDivElement>) {
    if (e.pointerType === 'mouse' && e.button !== 0) return;
    // No text selection starts under the pointer, and focus lands on the bar.
    e.preventDefault();
    e.currentTarget.focus({ preventScroll: true });
    try { e.currentTarget.setPointerCapture(e.pointerId); } catch { /* not capturable: moves still arrive */ }
    drag.current = { pointerId: e.pointerId, origin: coordinate(e), start: now, last: now };
    onDragChange?.(true);
  }

  function onPointerMove(e: PointerEvent<HTMLDivElement>) {
    const d = drag.current;
    if (!d || d.pointerId !== e.pointerId) return;
    const next = Math.round(clamp(d.start + coordinate(e) - d.origin, min, max));
    if (next === d.last) return;
    d.last = next;
    onChange(next);
  }

  function end(e: PointerEvent<HTMLDivElement>) {
    const d = drag.current;
    if (!d || d.pointerId !== e.pointerId) return;
    drag.current = null;
    if (e.currentTarget.hasPointerCapture?.(e.pointerId)) e.currentTarget.releasePointerCapture(e.pointerId);
    onDragChange?.(false);
  }

  function onKeyDown(e: KeyboardEvent<HTMLDivElement>) {
    const step = e.shiftKey ? BIG_STEP : STEP;
    const less = vertical ? 'ArrowLeft' : 'ArrowUp';
    const more = vertical ? 'ArrowRight' : 'ArrowDown';
    let next: number | null = null;
    if (e.key === less) next = now - step;
    else if (e.key === more) next = now + step;
    else if (e.key === 'Home') next = min;
    else if (e.key === 'End') next = max;
    else if (e.key === 'Enter') next = defaultValue;
    if (next === null) return;
    e.preventDefault();
    onChange(Math.round(clamp(next, min, max)));
  }

  return (
    <div
      role="separator"
      tabIndex={0}
      aria-orientation={orientation}
      aria-label={label}
      aria-controls={controls}
      aria-valuenow={now}
      aria-valuemin={Math.round(min)}
      aria-valuemax={Math.round(Math.max(min, max))}
      className={`mail-splitter is-${orientation}`}
      onPointerDown={onPointerDown}
      onPointerMove={onPointerMove}
      onPointerUp={end}
      onPointerCancel={end}
      onLostPointerCapture={end}
      onDoubleClick={() => onChange(Math.round(clamp(defaultValue, min, max)))}
      onKeyDown={onKeyDown}
      data-testid={testId}
    />
  );
}

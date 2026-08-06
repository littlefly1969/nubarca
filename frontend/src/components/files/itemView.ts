import { useCallback, useRef } from 'react';
import type { MouseEvent } from 'react';
import type { Entry } from './types';

// Shared contract for the grid card and list row so both render the same
// selection / activation / details affordances and the orchestrator wires one
// set of handlers regardless of view mode.
export interface ItemViewProps {
  entry: Entry;
  selected: boolean;
  // True when any item is selected — cards then surface their checkbox
  // permanently (selection mode) instead of on hover/focus only.
  selectionActive: boolean;
  // Primary click on the card body: open the folder, or open the file
  // (viewer for media, details otherwise). The orchestrator inspects modifier
  // keys on the event to fold ctrl/cmd/shift-click into selection instead.
  onActivate(entry: Entry, e: MouseEvent): void;
  // Selection checkbox toggle. Shift extends a range from the anchor.
  onToggleSelect(entry: Entry, e: MouseEvent): void;
  onDetails(entry: Entry): void;
  // Mobile: a long press enters selection mode and selects this item.
  onLongPress(entry: Entry): void;
}

const LONG_PRESS_MS = 450;

// Touch long-press → selection. Pointer move/end/cancel abort the timer so a
// scroll or tap never triggers it. Returns handlers to spread on the card.
export function useLongPress(onLongPress: () => void) {
  const timer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const cancel = useCallback(() => {
    if (timer.current) {
      clearTimeout(timer.current);
      timer.current = null;
    }
  }, []);

  const onTouchStart = useCallback(() => {
    cancel();
    timer.current = setTimeout(onLongPress, LONG_PRESS_MS);
  }, [cancel, onLongPress]);

  return {
    onTouchStart,
    onTouchEnd: cancel,
    onTouchMove: cancel,
    onTouchCancel: cancel,
  };
}

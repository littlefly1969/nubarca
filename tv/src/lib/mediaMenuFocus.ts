import { useCallback, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { tvDebug } from '../debug.ts';

// Focus OWNERSHIP for a native media wall and its MENU command overlay.
//
// The overlay used to be a plain absolutely-positioned bar whose first button
// asked for focus at mount time. Nothing else changed, so the grid underneath
// stayed a live focus destination: the command rail could be left by a D-pad
// press, and a virtualized row remounting could pull focus back into the media.
//
// Ownership is explicit here instead:
//
//   overlay hidden  → the grid owns focus
//   MENU pressed    → the exact focused item is remembered, the grid stops
//                     being a focus destination, the command rail owns focus
//   overlay closed  → ownership returns to the grid and the EXACT previously
//                     focused item is restored (MENU again, BACK, auto-hide and
//                     a command that closes the overlay all take this path)
//
// The position is remembered BOTH ways — by stable item id (survives a live
// Party append, a face-filter swap, paging, a row rebuild or a FlatList
// remount) and by index as the fallback when that item is gone.
//
// Grids without a command overlay (Personal Videos) use the same memory for
// their viewer round-trip; `menuOwnsFocus` simply stays false.

export interface HasId {
  id: string;
}

export interface TvGridFocusMemory {
  // Last tile the remote was on: index into the current display list…
  readonly index: number;
  // …and its stable id, which is what a restore prefers.
  readonly id: string | null;
  // One-shot request: the tile at this index should take native focus on its
  // next render. null means the grid already owns focus and no tile may hold a
  // preferred-focus flag (a stale one is what lets a remounting clipped row
  // steal focus).
  readonly restoreIndex: number | null;
  // True while the command rail owns focus.
  readonly menuOwnsFocus: boolean;
}

export const INITIAL_TV_GRID_FOCUS: TvGridFocusMemory = {
  index: 0,
  id: null,
  restoreIndex: 0,
  menuOwnsFocus: false,
};

// Where a restore should land: the same item when it still exists, otherwise
// the nearest still-valid index, otherwise the first tile. null when there is
// nothing to focus at all.
export function resolveTvGridRestoreIndex(
  memory: TvGridFocusMemory,
  items: readonly HasId[],
): number | null {
  if (items.length === 0) return null;
  if (memory.id !== null) {
    const byId = items.findIndex((item) => item.id === memory.id);
    if (byId >= 0) return byId;
  }
  return Math.max(0, Math.min(memory.index, items.length - 1));
}

// A tile reported native focus. Ignored while the command rail owns focus: the
// grid is not a focus destination then, so any such report is stale and must
// not overwrite the item the user will be returned to.
export function tvGridFocusOnTile(
  memory: TvGridFocusMemory,
  index: number,
  id: string,
): TvGridFocusMemory {
  if (memory.menuOwnsFocus) return memory;
  if (memory.index === index && memory.id === id && memory.restoreIndex === null) return memory;
  return { ...memory, index, id, restoreIndex: null };
}

// MENU opened the overlay: remember the exact item (already in `memory`) and
// drop the one-shot restore request, so no tile keeps a preferred-focus flag
// capable of pulling focus out of the command rail.
export function tvGridFocusOnMenuOpen(memory: TvGridFocusMemory): TvGridFocusMemory {
  if (memory.menuOwnsFocus && memory.restoreIndex === null) return memory;
  return { ...memory, menuOwnsFocus: true, restoreIndex: null };
}

// The overlay closed — MENU again, BACK, the idle auto-hide, or a command.
// Ownership returns to the grid at the resolved item.
export function tvGridFocusOnMenuClose(
  memory: TvGridFocusMemory,
  items: readonly HasId[],
): TvGridFocusMemory {
  const restoreIndex = resolveTvGridRestoreIndex(memory, items);
  return {
    ...memory,
    menuOwnsFocus: false,
    restoreIndex,
    index: restoreIndex ?? memory.index,
    id: restoreIndex === null ? memory.id : (items[restoreIndex]?.id ?? memory.id),
  };
}

// The app moves focus itself: a face-filter swap, a viewer/slideshow close, a
// bulk delete. An explicit new focus choice, not a D-pad move.
export function tvGridFocusRestoreTo(
  memory: TvGridFocusMemory,
  index: number,
  id: string | null,
): TvGridFocusMemory {
  return { ...memory, index, id, restoreIndex: index };
}

export interface TvGridFocusController {
  // The one-shot preferred-focus request. Everything else in the memory is
  // deliberately NOT render state: moving between tiles is the overwhelmingly
  // common event and must not re-render the screen.
  readonly restoreIndex: number | null;
  onTileFocused: (index: number, id: string) => void;
  restoreTo: (index: number, id: string | null) => void;
  // The latest memory, readable from a callback without re-rendering.
  read: () => TvGridFocusMemory;
}

// `menuOwnsFocus` is the caller's single source of truth (the overlay is
// visible AND the grid is the surface underneath it). The open/close
// transitions run from it in a LAYOUT effect, so the memory is already correct
// before the frame in which the command rail takes focus is drawn.
export function useTvGridFocusMemory(
  menuOwnsFocus: boolean,
  items: readonly HasId[],
): TvGridFocusController {
  const itemsRef = useRef(items);
  itemsRef.current = items;
  const memoryRef = useRef<TvGridFocusMemory>(INITIAL_TV_GRID_FOCUS);
  const [restoreIndex, setRestoreIndex] = useState<number | null>(
    INITIAL_TV_GRID_FOCUS.restoreIndex,
  );

  const apply = useCallback((next: TvGridFocusMemory) => {
    const previous = memoryRef.current;
    if (next === previous) return;
    memoryRef.current = next;
    if (next.restoreIndex !== previous.restoreIndex) setRestoreIndex(next.restoreIndex);
  }, []);

  const ownedRef = useRef(menuOwnsFocus);
  useLayoutEffect(() => {
    // Only real transitions; the initial run must not consume the mount-time
    // "focus the first tile" request.
    if (ownedRef.current === menuOwnsFocus) return;
    ownedRef.current = menuOwnsFocus;
    const next = menuOwnsFocus
      ? tvGridFocusOnMenuOpen(memoryRef.current)
      : tvGridFocusOnMenuClose(memoryRef.current, itemsRef.current);
    // Ordinals only, never an item id — see debug.ts.
    tvDebug('grid menu', menuOwnsFocus ? 'open' : 'close', 'restore', next.restoreIndex ?? -1);
    apply(next);
  }, [menuOwnsFocus, apply]);

  const onTileFocused = useCallback((index: number, id: string) => {
    const current = memoryRef.current;
    if (current.menuOwnsFocus) {
      // The named alarm for the exact regression this model exists to prevent:
      // a media tile taking native focus while the command rail owns it.
      tvDebug('grid focus ESCAPED into media under the open menu, index', index);
    }
    apply(tvGridFocusOnTile(current, index, id));
  }, [apply]);

  const restoreTo = useCallback((index: number, id: string | null) => {
    apply(tvGridFocusRestoreTo(memoryRef.current, index, id));
  }, [apply]);

  const read = useCallback(() => memoryRef.current, []);

  // The three callbacks are identity-stable, so a caller can depend on them in
  // an effect without re-running it on every focus move.
  return useMemo(
    () => ({ restoreIndex, onTileFocused, restoreTo, read }),
    [restoreIndex, onTileFocused, restoreTo, read],
  );
}

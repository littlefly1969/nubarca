import { useCallback, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import { MediaSelectionStore } from '../selection/mediaSelectionStore';

// One coherent selection model shared by the Gallery, album-scoped gallery,
// similar-photo results, and any People-filtered view. There is deliberately
// NO second selection system: every media surface drives this hook.
//
// Interaction model (Google Photos):
//   * Plain click / tap on a tile → the caller opens the viewer, in browse AND
//     in select mode. Selecting never costs the ability to look at a photo.
//     Returns 'open'.
//   * The round select control on each tile toggles that tile (Shift extends a
//     contiguous range from the anchor). It is always visible while a
//     selection is active, hover/focus-visible otherwise.
//   * Ctrl/Cmd + click → toggles that tile; anchors range selection here.
//   * Shift + click → selects the contiguous range from the anchor to here in
//     the current visible sorted order.
//   * A long press on touch starts the selection with that tile.
//   * Escape clears; Ctrl/Cmd+A ADDS every currently loaded id.
//
// The selected ids live in a MediaSelectionStore. A caller that passes one (the
// account store, an album's store) gets a selection that survives the screen:
// changing album, filter or page no longer drops it. Without one the hook keeps
// a store of its own, which is exactly the old component-local behaviour.
export interface MediaSelection {
  mode: 'browse' | 'select';
  enterSelection(id?: string): void;
  selected: ReadonlySet<string>;
  count: number;
  isSelectionActive: boolean;
  isSelected(id: string): boolean;
  clear(): void;
  // Replace the selection with exactly these ids.
  selectAll(ids: string[]): void;
  // Add these ids to whatever is already selected (Ctrl/Cmd+A on a wall).
  addAll(ids: readonly string[]): void;
  remove(ids: readonly string[]): void;
  // Toggle one id, from wherever it was shown (the viewer, the tray).
  toggle(id: string): void;
  // Tile click. Returns 'open' when the caller should open the viewer, or
  // 'selected' when the click mutated the selection instead.
  handleTileClick(
    id: string,
    index: number,
    orderedIds: readonly string[],
    modifiers: { ctrlOrMeta: boolean; shift: boolean },
  ): 'open' | 'selected';
  // Explicit select control (checkbox / touch). Always mutates. Shift extends
  // a contiguous range from the anchor.
  toggleViaControl(id: string, index: number, orderedIds: readonly string[], shift: boolean): void;
  // The store behind this selection, for surfaces that render it elsewhere.
  store: MediaSelectionStore;
}

export function useMediaSelection(
  { explicitMode = false, store: sharedStore = null }: { explicitMode?: boolean; store?: MediaSelectionStore | null } = {},
): MediaSelection {
  const [ownStore] = useState(() => new MediaSelectionStore());
  const store = sharedStore ?? ownStore;
  const snapshot = useSyncExternalStore(store.subscribe, store.getSnapshot, store.getSnapshot);
  const selected = snapshot.set;
  // Entering select mode with nothing picked yet is a local, per-screen gesture;
  // a store that already holds a selection starts every screen in select mode.
  const [explicitSelect, setExplicitSelect] = useState(false);
  const mode: 'browse' | 'select' = explicitSelect || selected.size > 0 ? 'select' : 'browse';
  // Identity of the last individually-toggled tile: paging and jumps change indexes.
  const anchorRef = useRef<string | null>(null);

  const clear = useCallback(() => {
    setExplicitSelect(false);
    anchorRef.current = null;
    store.clear();
  }, [store]);

  const selectAll = useCallback((ids: string[]) => {
    setExplicitSelect(ids.length > 0);
    store.replace(ids);
    anchorRef.current = ids[0] ?? null;
  }, [store]);

  const addAll = useCallback((ids: readonly string[]) => {
    if (ids.length === 0) return;
    setExplicitSelect(true);
    store.add(ids);
    anchorRef.current ??= ids[0];
  }, [store]);

  const remove = useCallback((ids: readonly string[]) => { store.remove(ids); }, [store]);

  const toggleOne = useCallback((id: string) => {
    setExplicitSelect(true);
    store.toggle(id);
    anchorRef.current = id;
  }, [store]);

  const selectRange = useCallback((index: number, orderedIds: readonly string[]) => {
    setExplicitSelect(true);
    const held = anchorRef.current === null ? -1 : orderedIds.indexOf(anchorRef.current);
    const anchor = held >= 0 ? held : index;
    const from = Math.min(anchor, index);
    const to = Math.max(anchor, index);
    store.add(orderedIds.slice(from, Math.min(to + 1, orderedIds.length)));
  }, [store]);

  const handleTileClick = useCallback(
    (
      id: string,
      index: number,
      orderedIds: readonly string[],
      modifiers: { ctrlOrMeta: boolean; shift: boolean },
    ): 'open' | 'selected' => {
      if (modifiers.shift && anchorRef.current !== null) {
        selectRange(index, orderedIds);
        return 'selected';
      }
      if (modifiers.ctrlOrMeta) {
        toggleOne(id);
        return 'selected';
      }
      return 'open';
    },
    [selectRange, toggleOne],
  );

  const toggleViaControl = useCallback(
    (id: string, index: number, orderedIds: readonly string[], shift: boolean) => {
      if (shift && anchorRef.current !== null) {
        selectRange(index, orderedIds);
        return;
      }
      toggleOne(id);
    },
    [selectRange, toggleOne],
  );

  const enterSelection = useCallback((id?: string) => {
    setExplicitSelect(true);
    if (id !== undefined) {
      anchorRef.current = id;
      store.add([id]);
    }
  }, [store]);

  return useMemo(() => ({
    mode,
    enterSelection,
    selected,
    count: selected.size,
    isSelectionActive: explicitMode ? mode === 'select' : selected.size > 0,
    isSelected: (id: string) => selected.has(id),
    clear,
    selectAll,
    addAll,
    remove,
    toggle: toggleOne,
    handleTileClick,
    toggleViaControl,
    store,
  }), [mode, enterSelection, selected, explicitMode, clear, selectAll, addAll, remove, toggleOne,
    handleTileClick, toggleViaControl, store]);
}

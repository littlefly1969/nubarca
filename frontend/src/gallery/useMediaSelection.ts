import { useCallback, useRef, useState } from 'react';

// One coherent selection model shared by the Gallery, album-scoped gallery,
// similar-photo results, and any People-filtered view. There is deliberately
// NO second selection system: every media surface drives this hook.
//
// Interaction model (see the product spec):
//   * Plain click on a tile → the caller opens the viewer (never mutates
//     selection). Returns 'open'.
//   * Ctrl/Cmd + click → toggles that tile; anchors range selection here.
//   * Shift + click → selects the contiguous range from the anchor to here in
//     the current visible sorted order.
//   * The per-tile checkbox (always visible on touch / in selection mode,
//     hover-visible on desktop) toggles that tile — Shift extends a range.
//   * Escape clears; Ctrl/Cmd+A selects all currently-visible ids.
// The unified workspace opts into explicit browse/select modes: ordinary taps
// toggle in select mode, even at zero selected items. Other callers retain their
// existing plain-click viewer behavior.
export interface MediaSelection {
  mode: 'browse' | 'select';
  enterSelection(id?: string): void;
  selected: ReadonlySet<string>;
  count: number;
  isSelectionActive: boolean;
  isSelected(id: string): boolean;
  clear(): void;
  selectAll(ids: string[]): void;
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
}

export function useMediaSelection({ explicitMode = false }: { explicitMode?: boolean } = {}): MediaSelection {
  const [selected, setSelected] = useState<Set<string>>(() => new Set());
  const [mode, setMode] = useState<'browse' | 'select'>('browse');
  // Identity of the last individually-toggled tile: paging and jumps change indexes.
  const anchorRef = useRef<string | null>(null);

  const clear = useCallback(() => {
    setMode('browse');
    anchorRef.current = null;
    setSelected((prev) => (prev.size === 0 ? prev : new Set()));
  }, []);

  const selectAll = useCallback((ids: string[]) => {
    setMode(ids.length > 0 ? 'select' : 'browse');
    setSelected(new Set(ids));
    anchorRef.current = ids[0] ?? null;
  }, []);

  const toggleOne = useCallback((id: string) => {
    setMode('select');
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
    anchorRef.current = id;
  }, []);

  const selectRange = useCallback((index: number, orderedIds: readonly string[]) => {
    setMode('select');
    const held = anchorRef.current === null ? -1 : orderedIds.indexOf(anchorRef.current);
    const anchor = held >= 0 ? held : index;
    const from = Math.min(anchor, index);
    const to = Math.max(anchor, index);
    setSelected((prev) => {
      const next = new Set(prev);
      for (let i = from; i <= to && i < orderedIds.length; i += 1) {
        next.add(orderedIds[i]);
      }
      return next;
    });
  }, []);

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
      if (modifiers.ctrlOrMeta || (explicitMode && mode === 'select')) {
        toggleOne(id);
        return 'selected';
      }
      return 'open';
    },
    [selectRange, toggleOne, explicitMode, mode],
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
    setMode('select');
    if (id !== undefined) {
      anchorRef.current = id;
      setSelected((prev) => new Set([...prev, id]));
    }
  }, []);

  return {
    mode,
    enterSelection,
    selected,
    count: selected.size,
    isSelectionActive: explicitMode ? mode === 'select' : selected.size > 0,
    isSelected: (id: string) => selected.has(id),
    clear,
    selectAll,
    handleTileClick,
    toggleViaControl,
  };
}

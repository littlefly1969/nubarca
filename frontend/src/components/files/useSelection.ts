import { useCallback, useMemo, useRef, useState } from 'react';
import type { Entry } from './types';
import { entryKey } from './types';

// Multi-select state for the file browser. Selection is keyed by namespaced
// entry id (folder:/file:) so it survives re-renders and is order-independent.
// Range selection ("shift") needs an anchor + the current ordered entry list,
// which the caller passes in on each interaction so the hook never holds a
// stale snapshot of the listing.

export interface SelectionApi {
  selected: ReadonlySet<string>;
  count: number;
  isSelected(key: string): boolean;
  // Plain checkbox / tap toggle of a single entry. Sets the range anchor.
  toggle(key: string): void;
  // Range select from the anchor to this entry (shift-click). Falls back to a
  // plain toggle when there is no anchor yet.
  selectRange(entries: readonly Entry[], key: string): void;
  selectOnly(key: string): void;
  clear(): void;
  // Drop keys that no longer exist (e.g. after a delete/move/reload) so the
  // selection bar never counts ghosts.
  retainExisting(existing: readonly Entry[]): void;
}

export function useSelection(): SelectionApi {
  const [selected, setSelected] = useState<ReadonlySet<string>>(() => new Set());
  const anchorRef = useRef<string | null>(null);

  const toggle = useCallback((key: string) => {
    anchorRef.current = key;
    setSelected((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });
  }, []);

  const selectOnly = useCallback((key: string) => {
    anchorRef.current = key;
    setSelected(new Set([key]));
  }, []);

  const selectRange = useCallback((entries: readonly Entry[], key: string) => {
    const anchor = anchorRef.current;
    if (anchor === null) {
      anchorRef.current = key;
      setSelected((prev) => {
        const next = new Set(prev);
        next.add(key);
        return next;
      });
      return;
    }
    const keys = entries.map(entryKey);
    const from = keys.indexOf(anchor);
    const to = keys.indexOf(key);
    if (from === -1 || to === -1) {
      anchorRef.current = key;
      setSelected((prev) => new Set(prev).add(key));
      return;
    }
    const [lo, hi] = from <= to ? [from, to] : [to, from];
    setSelected((prev) => {
      const next = new Set(prev);
      for (let i = lo; i <= hi; i++) next.add(keys[i]);
      return next;
    });
  }, []);

  const clear = useCallback(() => {
    anchorRef.current = null;
    setSelected((prev) => (prev.size === 0 ? prev : new Set()));
  }, []);

  const retainExisting = useCallback((existing: readonly Entry[]) => {
    const live = new Set(existing.map(entryKey));
    setSelected((prev) => {
      let changed = false;
      const next = new Set<string>();
      for (const key of prev) {
        if (live.has(key)) next.add(key);
        else changed = true;
      }
      return changed ? next : prev;
    });
  }, []);

  const isSelected = useCallback((key: string) => selected.has(key), [selected]);

  return useMemo(
    () => ({
      selected,
      count: selected.size,
      isSelected,
      toggle,
      selectRange,
      selectOnly,
      clear,
      retainExisting,
    }),
    [selected, isSelected, toggle, selectRange, selectOnly, clear, retainExisting],
  );
}

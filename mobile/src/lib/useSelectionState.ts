// React binding for IdSelection: re-render friendly snapshot + actions.
//
// SELECTION MODE IS EXPLICIT STATE, not `size > 0`.
//
// Deriving it from the count made two device-reported defects inevitable. The
// "select" button in the header called begin() and nothing happened — it could
// not enter a mode that only exists once something is already selected, so it
// was dead by construction. And a long press had to both enter the mode and
// pick the item, which meant two state writes racing over one stale snapshot;
// the first photo did not stick, so the user had to tap its circle afterwards.
//
// With an explicit flag, "in selection mode with nothing selected" is a real
// state — which is what a header button needs, and what lets a long press be
// ONE atomic transition.

import { useCallback, useState } from 'react';
import { IdSelection } from './selection';

interface SelectionSnapshot {
  selecting: boolean;
  selection: IdSelection;
}

const IDLE: SelectionSnapshot = { selecting: false, selection: new IdSelection() };

export function useSelectionState(): {
  ids: ReadonlySet<string>;
  count: number;
  selecting: boolean;
  /** Enter selection mode with nothing selected yet. */
  begin: () => void;
  /** Enter selection mode AND pick this item, in one transition. */
  beginWith: (id: string) => void;
  cancel: () => void;
  toggle: (id: string) => void;
  clear: () => void;
} {
  const [state, setState] = useState<SelectionSnapshot>(IDLE);

  const begin = useCallback(() => {
    setState((current) =>
      current.selecting ? current : { selecting: true, selection: new IdSelection() });
  }, []);

  // ONE update, computed from the previous state rather than from a snapshot
  // captured at render time — that race is what dropped the first long press.
  const beginWith = useCallback((id: string) => {
    setState((current) => ({
      selecting: true,
      selection: current.selecting
        ? new IdSelection().selectMany(current.selection.values()).toggle(id)
        : IdSelection.of(id),
    }));
  }, []);

  const toggle = useCallback((id: string) => {
    setState((current) => ({
      selecting: true,
      selection: new IdSelection().selectMany(current.selection.values()).toggle(id),
    }));
  }, []);

  // Leaving the mode clears what was picked: a selection that survived an exit
  // would reappear the next time the mode opened.
  const cancel = useCallback(() => setState(IDLE), []);
  const clear = useCallback(() => setState(IDLE), []);

  return {
    ids: new Set(state.selection.values()),
    count: state.selection.size,
    selecting: state.selecting,
    begin,
    beginWith,
    cancel,
    toggle,
    clear,
  };
}

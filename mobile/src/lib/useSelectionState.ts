// React binding for IdSelection: re-render friendly snapshot + actions.
import { useCallback, useState } from 'react';
import { IdSelection } from './selection';

export function useSelectionState(): {
  ids: ReadonlySet<string>;
  count: number;
  selecting: boolean;
  begin: () => void;
  cancel: () => void;
  toggle: (id: string) => void;
  clear: () => void;
} {
  const [selection, setSelection] = useState(new IdSelection());

  const sync = useCallback((next: IdSelection) => setSelection(next), []);

  const begin = useCallback(() => {
    if (selection.size === 0) setSelection(new IdSelection());
    // Selection mode is derived from size on screens; nothing else to store.
  }, [selection.size]);

  const cancel = useCallback(() => sync(new IdSelection()), [sync]);
  const toggle = useCallback(
    (id: string) => sync(new IdSelection().selectMany(selection.values()).toggle(id)),
    [selection, sync],
  );
  const clear = useCallback(() => sync(new IdSelection()), [sync]);

  return {
    ids: new Set(selection.values()),
    count: selection.size,
    selecting: selection.size > 0,
    begin,
    cancel,
    toggle,
    clear,
  };
}

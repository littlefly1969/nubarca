import { useSyncExternalStore } from 'react';
import type { TvSelectionStore } from './tvSelection';

// Re-render whenever the store changes; returns the store for convenience.
// A null store (a viewer with no selection, the assigned party wall) is inert.
export function useTvSelection(store: TvSelectionStore | null): number {
  return useSyncExternalStore(
    store ? store.subscribe : noopSubscribe,
    store ? store.getVersion : zero,
  );
}

const noopSubscribe = () => () => {};
const zero = () => 0;

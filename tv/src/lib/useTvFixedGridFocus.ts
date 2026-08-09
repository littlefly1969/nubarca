import { createRef, useCallback, useMemo, useRef, type RefObject } from 'react';
import type { View } from 'react-native';
import { tvGridNeighbor, type TvGridDirection } from './tvFixedGrid.ts';

// Native focus wiring for the deterministic fixed-column grid.
//
// The whole point of this hook is what it does NOT contain: no state, no
// re-render on navigation, no lane, no arrival direction. `targetsFor` depends
// only on (count, columns), so its identity — and every native nextFocus link
// derived from it — survives an entire auto-repeat burst untouched. That is
// what makes a rapid sequence and a slow one produce the same result; see the
// analysis at the top of tvFixedGrid.ts.
//
// Refs are keyed by FLAT INDEX rather than by item id. Pagination only ever
// APPENDS, so an index never changes meaning inside one query generation, and a
// new query generation replaces the registry wholesale. Index keys keep the
// registry aligned with the index-based focus graph with nothing to translate
// between them.

export interface TvFixedGridTargets {
  self: RefObject<View | null>;
  left?: RefObject<View | null>;
  right?: RefObject<View | null>;
  up?: RefObject<View | null>;
  down?: RefObject<View | null>;
}

export interface TvFixedGridFocus {
  targetsFor: (index: number) => TvFixedGridTargets;
  // Drop every ref. Called when a new query generation replaces the items, so
  // the registry cannot keep growing across filter changes for the lifetime of
  // the screen.
  reset: () => void;
}

const DIRECTIONS: readonly TvGridDirection[] = ['left', 'right', 'up', 'down'];

export function useTvFixedGridFocus(count: number, columns: number): TvFixedGridFocus {
  const refs = useRef(new Map<number, RefObject<View | null>>());

  const refFor = useCallback((index: number): RefObject<View | null> => {
    let ref = refs.current.get(index);
    if (!ref) {
      ref = createRef<View>();
      refs.current.set(index, ref);
    }
    return ref;
  }, []);

  const reset = useCallback(() => {
    refs.current.clear();
  }, []);

  const targetsFor = useCallback((index: number): TvFixedGridTargets => {
    const targets: TvFixedGridTargets = { self: refFor(index) };
    for (const direction of DIRECTIONS) {
      const neighbor = tvGridNeighbor(index, direction, count, columns);
      // A move that leaves the grid gets NO link. Leaving it undefined is
      // deliberate: Android then applies its own search and finds the screen's
      // chrome if any is focusable, instead of the grid trapping focus.
      if (neighbor !== null) targets[direction] = refFor(neighbor);
    }
    return targets;
  }, [count, columns, refFor]);

  return useMemo(() => ({ targetsFor, reset }), [targetsFor, reset]);
}

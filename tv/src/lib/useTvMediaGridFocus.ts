import {
  createRef,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  type RefObject,
} from 'react';
import type { View } from 'react-native';
import {
  buildTvMediaGridModel,
  type TvMediaGridDirection,
  type TvMediaGridRow,
} from './tvMediaGrid.ts';

export interface TvMediaGridTargets {
  self: RefObject<View | null>;
  left: RefObject<View | null>;
  right: RefObject<View | null>;
  up: RefObject<View | null>;
  down: RefObject<View | null>;
}

const DIRECTIONS: readonly TvMediaGridDirection[] = ['left', 'right', 'up', 'down'];

const itemId = <T extends { id: string }>(item: T) => item.id;

export function useTvMediaGridFocus<T extends { id: string }>(
  rows: readonly TvMediaGridRow<T>[],
) {
  const refs = useRef(new Map<string, RefObject<View | null>>());
  const readyIds = useRef(new Set<string>());
  const rowsRef = useRef(rows);
  rowsRef.current = rows;

  const refFor = useCallback((id: string) => {
    let ref = refs.current.get(id);
    if (!ref) {
      ref = createRef<View>();
      refs.current.set(id, ref);
    }
    return ref;
  }, []);

  for (const row of rows) {
    for (const tile of row.tiles) refFor(tile.item.id);
  }

  const model = useMemo(() => buildTvMediaGridModel(rows, itemId), [rows]);
  const [readyRows, setReadyRows] = useState<ReadonlySet<string>>(() => new Set());

  useEffect(() => {
    const liveIds = new Set(rows.flatMap((row) => row.tiles.map((tile) => tile.item.id)));
    for (const id of readyIds.current) {
      if (!liveIds.has(id)) readyIds.current.delete(id);
    }
    for (const id of refs.current.keys()) {
      if (!liveIds.has(id)) refs.current.delete(id);
    }
    const next = new Set(
      rows
        .filter((row) => row.tiles.every((tile) => readyIds.current.has(tile.item.id)))
        .map((row) => row.key),
    );
    setReadyRows((current) => (
      current.size === next.size && [...current].every((key) => next.has(key)) ? current : next
    ));
  }, [rows]);

  const onPreviewReady = useCallback((rowKey: string, id: string) => {
    readyIds.current.add(id);
    const row = rowsRef.current.find((candidate) => candidate.key === rowKey);
    if (!row || !row.tiles.every((tile) => readyIds.current.has(tile.item.id))) return;
    setReadyRows((current) => {
      if (current.has(rowKey)) return current;
      return new Set(current).add(rowKey);
    });
  }, []);

  const isRowReady = useCallback((rowKey: string) => readyRows.has(rowKey), [readyRows]);

  const targetsFor = useCallback((id: string): TvMediaGridTargets => {
    const self = refFor(id);
    const link = model.links.get(id);
    const targets = { self } as TvMediaGridTargets;
    for (const direction of DIRECTIONS) {
      const targetId = link?.[direction];
      const targetRow = targetId ? model.rowKeyById.get(targetId) : undefined;
      targets[direction] = targetId && targetRow && readyRows.has(targetRow)
        ? refFor(targetId)
        : self;
    }
    return targets;
  }, [model, readyRows, refFor]);

  return useMemo(
    () => ({ targetsFor, isRowReady, onPreviewReady }),
    [targetsFor, isRowReady, onPreviewReady],
  );
}

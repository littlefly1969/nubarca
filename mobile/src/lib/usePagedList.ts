// React binding for the pure PagedList state machine. Screens consume one
// hook; all pagination race rules live in the tested class below it.

import { useCallback, useRef, useState } from 'react';
import {
  PagedList,
  type FetchPage,
  type Page,
  type PagedSnapshot,
} from './pagination';

export function usePagedList<TItem>(
  keyOf: (item: TItem) => string,
  fetcher: FetchPage<TItem>,
): {
  snapshot: PagedSnapshot<TItem>;
  refresh: () => Promise<void>;
  loadMore: () => Promise<void>;
  patchItem: (key: string, patch: (item: TItem) => TItem) => void;
  removeItems: (keys: ReadonlySet<string>) => void;
  /** Version counter that increments whenever items change (list re-render). */
  version: number;
} {
  const listRef = useRef<PagedList<TItem> | null>(null);
  if (listRef.current === null) {
    listRef.current = new PagedList<TItem>(keyOf);
  }
  const list = listRef.current;

  const [snapshot, setSnapshot] = useState<PagedSnapshot<TItem>>(() =>
    list.snapshot(),
  );
  const [version, setVersion] = useState(0);

  const sync = useCallback(() => {
    setSnapshot({ ...list.snapshot() });
    setVersion((v) => v + 1);
  }, [list]);

  const refresh = useCallback(async () => {
    await list.refresh(fetcher);
    sync();
  }, [list, fetcher, sync]);

  const loadMore = useCallback(async () => {
    await list.loadMore(fetcher);
    sync();
  }, [list, fetcher, sync]);

  const patchItem = useCallback(
    (key: string, patch: (item: TItem) => TItem) => {
      list.patchItem(key, patch);
      sync();
    },
    [list, sync],
  );

  const removeItems = useCallback(
    (keys: ReadonlySet<string>) => {
      list.removeItems(keys);
      sync();
    },
    [list, sync],
  );

  return { snapshot, refresh, loadMore, patchItem, removeItems, version };
}

export type { FetchPage, Page };

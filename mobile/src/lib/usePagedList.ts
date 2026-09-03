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
  retryFailed: () => Promise<void>;
  patchItem: (key: string, patch: (item: TItem) => TItem) => void;
  removeItems: (keys: ReadonlySet<string>) => void;
  /** Version counter that increments whenever items change (list re-render). */
} {
  const listRef = useRef<PagedList<TItem> | null>(null);
  if (listRef.current === null) {
    listRef.current = new PagedList<TItem>(keyOf);
  }
  const list = listRef.current;

  const [snapshot, setSnapshot] = useState<PagedSnapshot<TItem>>(() =>
    list.snapshot(),
  );

  // A fresh snapshot object each time, holding whatever the list currently
  // has. There is deliberately no counter beside it: the item array changes
  // identity when its contents change (see PagedList), so a second
  // invalidation signal would only be a way to paper over a data layer that
  // had stopped telling the truth.
  const sync = useCallback(() => {
    setSnapshot({ ...list.snapshot() });
  }, [list]);

  // IMMEDIATE PHASES (acceptance fix): the state machine flips its phase
  // SYNCHRONOUSLY before the first await — syncing right after starting the
  // promise is what lets the UI show loading/refreshing/loadingMore NOW,
  // instead of discovering them only when the operation settles.
  const refresh = useCallback(async () => {
    const pending = list.refresh(fetcher);
    sync();
    await pending;
    sync();
  }, [list, fetcher, sync]);

  const loadMore = useCallback(async () => {
    const pending = list.loadMore(fetcher);
    sync();
    await pending;
    sync();
  }, [list, fetcher, sync]);

  // The footer's retry must repeat the operation that ACTUALLY failed:
  // a failed refresh re-runs refresh (a bare loadMore would be a no-op with
  // its dropped cursor); a failed loadMore re-fetches the same page.
  const retryFailed = useCallback(async () => {
    const target = list.snapshot().retryTarget;
    if (target === 'loadMore') await loadMore();
    else await refresh();
  }, [list, loadMore, refresh]);

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

  return { snapshot, refresh, loadMore, retryFailed, patchItem, removeItems };
}

export type { FetchPage, Page };

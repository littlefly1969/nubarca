import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { searchMailMessages } from './mailApi';
import { filtersToApiQuery, type MailFilters } from './mailQuery';
import type { MailMessageSummary } from './types';

export const MAIL_PAGE_SIZE = 50;

export type MailListStatus = 'loading' | 'ready' | 'error';

export interface MailMessageList {
  status: MailListStatus;
  items: MailMessageSummary[];
  nextCursor: string | null;
  bodySearchIncomplete: boolean;
  loadingMore: boolean;
  loadMoreError: boolean;
  loadMore: () => void;
  reload: () => void;
  remove: (id: string) => void;
}

// One keyset-paged message list for a set of filters.
//
// Never loads an archive: the first page is fetched when the filters change and
// every further page only on request, with the server's opaque `nextCursor`.
// Order is whatever the server returned (date desc, then id); pages are only
// appended, and a row the owner deleted is dropped locally.
export function useMailMessages(filters: MailFilters | null): MailMessageList {
  const { invalidateAuth } = useAuth();
  const [status, setStatus] = useState<MailListStatus>('loading');
  const [items, setItems] = useState<MailMessageSummary[]>([]);
  const [nextCursor, setNextCursor] = useState<string | null>(null);
  const [bodySearchIncomplete, setBodySearchIncomplete] = useState(false);
  const [loadingMore, setLoadingMore] = useState(false);
  const [loadMoreError, setLoadMoreError] = useState(false);
  const [reloadToken, setReloadToken] = useState(0);
  // The request identity the current rows belong to; a late page for an older
  // query is ignored.
  const key = filters === null ? null : filtersToApiQuery(filters).toString();
  const keyRef = useRef(key);
  keyRef.current = key;
  const filtersRef = useRef(filters);
  filtersRef.current = filters;

  useEffect(() => {
    if (key === null || filtersRef.current === null) return;
    const controller = new AbortController();
    setStatus('loading');
    setItems([]);
    setNextCursor(null);
    setBodySearchIncomplete(false);
    setLoadMoreError(false);
    searchMailMessages(filtersRef.current, null, controller.signal, MAIL_PAGE_SIZE)
      .then((page) => {
        if (controller.signal.aborted) return;
        setItems(page.items ?? []);
        setNextCursor(page.nextCursor ?? null);
        setBodySearchIncomplete(page.bodySearchIncomplete === true);
        setStatus('ready');
      })
      .catch((err: unknown) => {
        if (controller.signal.aborted) return;
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        setStatus('error');
      });
    return () => controller.abort();
  }, [key, reloadToken, invalidateAuth]);

  const loadMore = useCallback(() => {
    const current = filtersRef.current;
    if (!current || !nextCursor || loadingMore) return;
    const requestKey = keyRef.current;
    setLoadingMore(true);
    setLoadMoreError(false);
    searchMailMessages(current, nextCursor, undefined, MAIL_PAGE_SIZE)
      .then((page) => {
        if (keyRef.current !== requestKey) return;
        setItems((prev) => {
          const seen = new Set(prev.map((m) => m.id));
          return [...prev, ...(page.items ?? []).filter((m) => !seen.has(m.id))];
        });
        setNextCursor(page.nextCursor ?? null);
      })
      .catch((err: unknown) => {
        if (err instanceof ApiError && err.status === 401) { invalidateAuth(); return; }
        if (keyRef.current === requestKey) setLoadMoreError(true);
      })
      .finally(() => {
        if (keyRef.current === requestKey) setLoadingMore(false);
      });
  }, [nextCursor, loadingMore, invalidateAuth]);

  const reload = useCallback(() => setReloadToken((n) => n + 1), []);
  const remove = useCallback((id: string) => {
    setItems((prev) => prev.filter((m) => m.id !== id));
  }, []);

  return { status, items, nextCursor, bodySearchIncomplete, loadingMore, loadMoreError, loadMore, reload, remove };
}

import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  getDirectoryChildren,
  type DirectorySortField,
  type FileSummary,
  type FolderSummary,
  type SortDirection,
} from '@nubarca/api-client';
import { useAuth } from '../../auth/useAuth';

// Drives the Files UI v2 listing for one folder + sort: fetches the first page
// (folders + first file page), then appends further file pages on demand via
// the opaque cursor. A fresh AbortController is created on every (folder, sort)
// change so rapid navigation never lets a stale response overwrite the current
// one. Mirrors the gallery's load-phase model but scoped to one directory.

export type ListingStatus = 'loading' | 'ready' | 'error';

export interface DirectoryListing {
  status: ListingStatus;
  folders: FolderSummary[];
  files: FileSummary[];
  hasMore: boolean;
  loadingMore: boolean;
  moreError: boolean;
  // True only for the "folder no longer exists" terminal error (404), so the
  // caller can show a distinct message + offer navigating up.
  missing: boolean;
  reload(): void;
  loadMore(): void;
}

interface ListingState {
  status: ListingStatus;
  folders: FolderSummary[];
  files: FileSummary[];
  cursor: string | null;
  hasMore: boolean;
  loadingMore: boolean;
  moreError: boolean;
  missing: boolean;
}

const INITIAL: ListingState = {
  status: 'loading',
  folders: [],
  files: [],
  cursor: null,
  hasMore: false,
  loadingMore: false,
  moreError: false,
  missing: false,
};

const PAGE_LIMIT = 100;

export function useDirectoryListing(
  folderId: string | null,
  sort: DirectorySortField,
  direction: SortDirection,
): DirectoryListing {
  const { invalidateAuth } = useAuth();
  const [state, setState] = useState<ListingState>(INITIAL);
  // Bumped to force a reload of the current page-1 (after a mutation).
  const [reloadToken, setReloadToken] = useState(0);
  // Guards loadMore against the in-flight first-page request and stale closures.
  const cursorRef = useRef<string | null>(null);
  const loadingMoreRef = useRef(false);

  const handleError = useCallback(
    (err: unknown): { missing: boolean } => {
      if (err instanceof ApiError && err.status === 401) {
        invalidateAuth();
        return { missing: false };
      }
      if (err instanceof ApiError && err.status === 404) {
        return { missing: true };
      }
      return { missing: false };
    },
    [invalidateAuth],
  );

  // First page whenever folder / sort / direction / reloadToken changes.
  useEffect(() => {
    const controller = new AbortController();
    cursorRef.current = null;
    loadingMoreRef.current = false;
    setState({ ...INITIAL, status: 'loading' });

    getDirectoryChildren(folderId, { sort, direction, limit: PAGE_LIMIT }, controller.signal)
      .then((data) => {
        cursorRef.current = data.nextCursor ?? null;
        setState({
          status: 'ready',
          folders: data.folders,
          files: data.files,
          cursor: data.nextCursor ?? null,
          hasMore: data.hasMore === true,
          loadingMore: false,
          moreError: false,
          missing: false,
        });
      })
      .catch((err: unknown) => {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        const { missing } = handleError(err);
        setState({ ...INITIAL, status: 'error', missing });
      });

    return () => controller.abort();
  }, [folderId, sort, direction, reloadToken, handleError]);

  const loadMore = useCallback(() => {
    const cursor = cursorRef.current;
    if (cursor === null || loadingMoreRef.current) return;
    loadingMoreRef.current = true;
    setState((prev) => ({ ...prev, loadingMore: true, moreError: false }));

    getDirectoryChildren(folderId, { sort, direction, limit: PAGE_LIMIT, cursor })
      .then((data) => {
        cursorRef.current = data.nextCursor ?? null;
        loadingMoreRef.current = false;
        setState((prev) => ({
          ...prev,
          files: [...prev.files, ...data.files],
          cursor: data.nextCursor ?? null,
          hasMore: data.hasMore === true,
          loadingMore: false,
        }));
      })
      .catch((err: unknown) => {
        loadingMoreRef.current = false;
        if (err instanceof DOMException && err.name === 'AbortError') return;
        handleError(err);
        setState((prev) => ({ ...prev, loadingMore: false, moreError: true }));
      });
  }, [folderId, sort, direction, handleError]);

  const reload = useCallback(() => setReloadToken((t) => t + 1), []);

  return {
    status: state.status,
    folders: state.folders,
    files: state.files,
    hasMore: state.hasMore,
    loadingMore: state.loadingMore,
    moreError: state.moreError,
    missing: state.missing,
    reload,
    loadMore,
  };
}

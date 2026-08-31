// The screen-side binding for the shared filter model.
//
// It owns the APPLIED identity and derives everything a workspace screen
// needs: the page fetcher, the chips, and the People labels the chips display.
//
// QUERY LIFECYCLE (§19). The fetcher's identity is keyed on the query
// GENERATION, so committing a filter produces a new fetcher; the screen's
// refresh effect then restarts the list, which drops the cursor, clears the
// accumulator and abandons the in-flight request through PagedList's existing
// token discipline. Pages of an old query can never be appended to a new one,
// and the cursor stays server-authoritative — nothing here synthesizes one.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type {
  MediaKindScope,
  MediaSortField,
  MediaWorkspaceFilters,
  MediaWorkspaceIdentity,
  MediaWorkspaceSource,
} from '@nubarca/contracts';
import {
  chipsFor,
  generationOf,
  initialIdentity,
  pageQuery,
  referencedPersonIds,
  withChipCleared,
  withFilters,
  withFiltersCleared,
  type FilterChipDescriptor,
  type FilterChipKind,
} from './mediaFilterState.ts';
import { listMedia, listAlbumMedia, type MediaListResponse } from '../api/media.ts';
import { listPeopleForFilter, type PersonSummary } from '../api/people.ts';

export interface MediaFiltersBinding {
  identity: MediaWorkspaceIdentity;
  /** Changes exactly when the result set would change. */
  generation: string;
  chips: FilterChipDescriptor[];
  /** personId -> summary, for resolving chip labels. */
  people: ReadonlyMap<string, PersonSummary>;
  fetchPage: (cursor: string | null, signal: AbortSignal) => Promise<MediaListResponse>;
  apply: (
    filters: MediaWorkspaceFilters,
    sort: MediaSortField,
    direction: 'asc' | 'desc',
  ) => void;
  removeChip: (kind: FilterChipKind) => void;
  clearAll: () => void;
}

export function useMediaFilters(
  kind: MediaKindScope,
  pageSize: number,
  source: MediaWorkspaceSource = { kind: 'library' },
): MediaFiltersBinding {
  const albumId = source.kind === 'album' ? source.albumId : null;
  const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(() =>
    initialIdentity(kind, source));

  const generation = generationOf(identity);
  // The fetcher must not change identity on every render, only when the query
  // does — otherwise the screen's refresh effect would loop.
  const identityRef = useRef(identity);
  identityRef.current = identity;

  const fetchPage = useCallback(
    (cursor: string | null, signal: AbortSignal) => {
      const query = pageQuery(identityRef.current, cursor, pageSize);
      return albumId === null
        ? listMedia(query, signal)
        : listAlbumMedia(albumId, query, signal);
    },
    // `generation` is the real dependency: a new query needs a new fetcher.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [generation, pageSize, albumId],
  );

  const chips = useMemo(() => chipsFor(identity), [identity]);

  // Person labels for the chips. Loaded only once somebody is actually
  // filtered by — an unfiltered library must not pull the People catalogue.
  const referenced = referencedPersonIds(identity.filters).join(',');
  const [people, setPeople] = useState<ReadonlyMap<string, PersonSummary>>(new Map());
  useEffect(() => {
    if (referenced.length === 0) return undefined;
    const controller = new AbortController();
    listPeopleForFilter(controller.signal).then(
      (loaded) => {
        if (controller.signal.aborted) return;
        setPeople(new Map(loaded.map((p) => [p.personId, p])));
      },
      () => { /* a chip falls back to "unnamed"; it is a label, not the filter */ },
    );
    return () => controller.abort();
  }, [referenced]);

  return {
    identity,
    generation,
    chips,
    people,
    fetchPage,
    apply: useCallback((filters, sort, direction) => {
      setIdentity((current) => ({ ...withFilters(current, filters), sort, direction }));
    }, []),
    removeChip: useCallback((chipKind: FilterChipKind) => {
      setIdentity((current) => withChipCleared(current, chipKind));
    }, []),
    clearAll: useCallback(() => {
      setIdentity((current) => withFiltersCleared(current));
    }, []),
  };
}

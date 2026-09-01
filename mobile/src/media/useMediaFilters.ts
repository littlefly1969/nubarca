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
import { isSemanticActive } from '@nubarca/contracts';
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
import {
  listMedia,
  listAlbumMedia,
  searchSemanticMedia,
  type MediaListResponse,
} from '../api/media.ts';
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
    async (cursor: string | null, signal: AbortSignal): Promise<MediaListResponse> => {
      const current = identityRef.current;

      // SEMANTIC (§10) is a different backend operation, not a filter on the
      // same one: relevance ranking carries its own cursor, so the unified
      // listing cannot simply take a visual term. The separation the backend
      // makes is preserved rather than flattened.
      //
      // `isSemanticActive` is the single place that knows WHERE a visual query
      // applies — the semantic route is library-scoped, so inside an album it
      // applies to the photo tab only. Asking it here means the sheet, the
      // chips and this fetch can never disagree about whether it is on.
      if (isSemanticActive(current)) {
        const page = await searchSemanticMedia({
          q: current.filters.photo.visualQuery.trim(),
          kind: current.mediaKind,
          limit: pageSize,
          cursor,
          favorite: current.filters.common.favorite ?? undefined,
          minRating: current.filters.common.minRating ?? undefined,
          dateTakenFrom: current.filters.common.dateTakenFrom || undefined,
          dateTakenTo: current.filters.common.dateTakenTo || undefined,
          albumMembership:
            current.source.kind === 'library' ? current.filters.common.albumMembership : undefined,
        }, signal);
        // The grid renders media; the temporal evidence a video result carries
        // is not something this list surface shows, so it is dropped here
        // rather than smuggled through in a widened item type.
        return {
          items: page.items.map((result) => result.media),
          limit: pageSize,
          count: page.items.length,
          nextCursor: page.nextCursor,
          hasMore: page.hasMore,
          total: page.total,
          photoCount: 0,
          videoCount: 0,
        };
      }

      const query = pageQuery(current, cursor, pageSize);
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

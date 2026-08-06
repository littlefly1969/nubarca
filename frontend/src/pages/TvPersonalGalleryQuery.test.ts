import { describe, expect, it } from 'vitest';
import type { TvInterpretDraft } from '@nubarca/api-client';
import {
  activeFilterCount,
  draftToApplied,
  EMPTY_QUERY,
  toWireQuery,
  type AppliedQuery,
} from './TvPersonalGallery';

describe('/tv Personal Gallery query parity', () => {
  it('serializes the default query with the shared paging and sort defaults', () => {
    expect(toWireQuery(EMPTY_QUERY, null)).toEqual({
      q: undefined,
      sort: 'created',
      direction: 'desc',
      limit: 50,
      cursor: undefined,
      favorite: undefined,
      minRating: undefined,
      hasGps: undefined,
      dateTakenFrom: undefined,
      dateTakenTo: undefined,
      collapseDuplicates: undefined,
      includePeople: undefined,
      excludePeople: undefined,
      includePeopleMode: undefined,
      semanticQuery: undefined,
      semanticTopK: undefined,
    });
  });

  it('projects every manual and semantic dimension to the backend field names', () => {
    const query: AppliedQuery = {
      q: 'mare estate',
      sort: 'name',
      direction: 'asc',
      favorite: false,
      minRating: 3,
      hasGps: true,
      dateFrom: '2023-06-01',
      dateTo: '2023-06-30',
      collapseDuplicates: true,
      includePeople: ['p1', 'p3'],
      excludePeople: ['p2'],
      includePeopleMode: 'any',
      semanticQuery: 'tramonto sulla spiaggia',
      semanticTopK: 300,
    };
    expect(toWireQuery(query, 'CUR')).toMatchObject({
      q: 'mare estate', sort: 'name', direction: 'asc', cursor: 'CUR',
      favorite: false, minRating: 3, hasGps: true,
      dateTakenFrom: '2023-06-01T00:00:00Z',
      dateTakenTo: '2023-06-30T23:59:59Z',
      collapseDuplicates: true,
      includePeople: ['p1', 'p3'], excludePeople: ['p2'], includePeopleMode: 'any',
      semanticQuery: 'tramonto sulla spiaggia', semanticTopK: 300,
    });
    // Search, semantic content and the date interval each count as one visible
    // dimension, matching the native TV summary.
    expect(activeFilterCount(query)).toBe(9);
  });

  it('omits include mode for an exclude-only people query', () => {
    const query = { ...EMPTY_QUERY, excludePeople: ['p9'], includePeopleMode: 'any' as const };
    expect(toWireQuery(query, null)).toMatchObject({
      includePeople: undefined,
      includePeopleMode: undefined,
      excludePeople: ['p9'],
    });
  });

  it('maps parser drafts and parser clear without changing wire semantics', () => {
    const parsed: TvInterpretDraft = {
      version: 1,
      operation: 'replace',
      peopleInclude: ['p1'],
      peopleExclude: ['p2'],
      peopleMatch: 'all',
      favorite: true,
      minRating: 4,
      hasGps: null,
      dateTakenFrom: '2024-06-01T00:00:00Z',
      dateTakenTo: '2024-08-31T23:59:59Z',
      collapseDuplicates: true,
      sort: null,
      sortDirection: null,
      metadataSearch: 'vacanze',
      semanticQuery: 'Anna al mare',
      semanticQueryEnglish: null,
      semanticTopK: 300,
    };
    expect(toWireQuery(draftToApplied(parsed), null)).toMatchObject({
      q: 'vacanze', favorite: true, minRating: 4,
      includePeople: ['p1'], excludePeople: ['p2'], includePeopleMode: 'all',
      dateTakenFrom: '2024-06-01T00:00:00Z',
      dateTakenTo: '2024-08-31T23:59:59Z',
      semanticQuery: 'Anna al mare', semanticTopK: 300,
    });
    expect(draftToApplied({ ...parsed, operation: 'clear' })).toEqual(EMPTY_QUERY);
  });
});

import { describe, expect, it } from 'vitest';
import type { GalleryInterpretDraft } from '@nubarca/api-client';
import {
  EMPTY_GALLERY_QUERY,
  applyInterpretDraft,
  buildFilterChips,
  clearChip,
  dateInputToIso,
  hasActiveQuery,
  isSemanticActive,
  isoToDateInput,
  queryFromUrlParams,
  queryToCurrentFilterState,
  queryToListQuery,
  queryToUrlParams,
  type GalleryQuery,
} from './galleryQuery';

const draft = (over: Partial<GalleryInterpretDraft> = {}): GalleryInterpretDraft => ({
  version: 1,
  operation: 'replace',
  peopleInclude: [],
  peopleExclude: [],
  peopleMatch: 'all',
  favorite: null,
  minRating: null,
  hasGps: null,
  dateTakenFrom: null,
  dateTakenTo: null,
  collapseDuplicates: null,
  sort: null,
  sortDirection: null,
  metadataSearch: null,
  semanticQuery: null,
  semanticQueryEnglish: null,
  semanticTopK: 300,
  ...over,
});

describe('queryToListQuery', () => {
  it('maps metadata + visual fields independently and coexisting', () => {
    const wire = queryToListQuery(
      { ...EMPTY_GALLERY_QUERY, metadataQuery: 'vacation', visualQuery: 'beach at sunset', semanticTopK: 300 },
      null,
    );
    expect(wire.q).toBe('vacation');
    expect(wire.semanticQuery).toBe('beach at sunset');
    expect(wire.semanticTopK).toBe(300);
  });

  it('omits semanticQuery/topK when visual query is empty', () => {
    const wire = queryToListQuery({ ...EMPTY_GALLERY_QUERY, metadataQuery: 'x', semanticTopK: 300 }, null);
    expect(wire.semanticQuery).toBeUndefined();
    expect(wire.semanticTopK).toBeUndefined();
  });

  it('attaches cursor, people mode only when include people present, and all physical filters', () => {
    const q: GalleryQuery = {
      ...EMPTY_GALLERY_QUERY,
      favorite: true,
      minRating: 4,
      hasGps: false,
      dateTakenFrom: '2024-06-01T00:00:00.000Z',
      dateTakenTo: '2024-08-31T00:00:00.000Z',
      collapseDuplicates: true,
      includePeople: ['p1'],
      includePeopleMode: 'any',
    };
    const wire = queryToListQuery(q, 'CUR');
    expect(wire.cursor).toBe('CUR');
    expect(wire.favorite).toBe(true);
    expect(wire.minRating).toBe(4);
    expect(wire.hasGps).toBe(false);
    expect(wire.dateTakenFrom).toBe('2024-06-01T00:00:00.000Z');
    expect(wire.collapseDuplicates).toBe(true);
    expect(wire.includePeopleMode).toBe('any');
  });
});

describe('isSemanticActive forces relevance display', () => {
  it('is true only with a visual query', () => {
    expect(isSemanticActive(EMPTY_GALLERY_QUERY)).toBe(false);
    expect(isSemanticActive({ ...EMPTY_GALLERY_QUERY, metadataQuery: 'text' })).toBe(false);
    expect(isSemanticActive({ ...EMPTY_GALLERY_QUERY, visualQuery: 'sunset' })).toBe(true);
  });
});

describe('applyInterpretDraft', () => {
  it('adopts the full draft as the target state (replace)', () => {
    const q = applyInterpretDraft(EMPTY_GALLERY_QUERY, draft({
      metadataSearch: 'trip',
      semanticQuery: 'beach',
      favorite: true,
      minRating: 3,
      peopleInclude: ['a', 'b'],
      peopleMatch: 'any',
      sort: 'datetaken',
      sortDirection: 'asc',
    }));
    expect(q.metadataQuery).toBe('trip');
    expect(q.visualQuery).toBe('beach');
    expect(q.semanticTopK).toBe(300);
    expect(q.favorite).toBe(true);
    expect(q.minRating).toBe(3);
    expect(q.includePeople).toEqual(['a', 'b']);
    expect(q.includePeopleMode).toBe('any');
    expect(q.sort).toBe('datetaken');
    expect(q.direction).toBe('asc');
  });

  it('clear resets to empty', () => {
    const q = applyInterpretDraft(
      { ...EMPTY_GALLERY_QUERY, metadataQuery: 'x', favorite: true },
      draft({ operation: 'clear' }),
    );
    expect(q).toEqual(EMPTY_GALLERY_QUERY);
  });

  it('drops topK when there is no visual query', () => {
    const q = applyInterpretDraft(EMPTY_GALLERY_QUERY, draft({ semanticQuery: null, semanticTopK: 300 }));
    expect(q.semanticTopK).toBe(0);
  });
});

describe('URL persistence', () => {
  it('round-trips shareable fields and omits visual/discovery fields', () => {
    const q: GalleryQuery = {
      ...EMPTY_GALLERY_QUERY,
      metadataQuery: 'beach',
      visualQuery: 'sunset', // owner-private, must NOT persist
      favorite: true, // discovery, must NOT persist
      sort: 'name',
      direction: 'asc',
      includePeople: ['p1', 'p2'],
      includePeopleMode: 'any',
      excludePeople: ['p3'],
    };
    const sp = queryToUrlParams(q);
    expect(sp.get('q')).toBe('beach');
    expect(sp.get('semanticQ')).toBeNull();
    expect(sp.has('favorite')).toBe(false);
    expect(sp.get('sort')).toBe('name');
    expect(sp.get('includePeople')).toBe('p1,p2');
    expect(sp.get('includePeopleMode')).toBe('any');
    expect(sp.get('excludePeople')).toBe('p3');

    const restored = queryFromUrlParams(sp);
    expect(restored.metadataQuery).toBe('beach');
    expect(restored.visualQuery).toBe('');
    expect(restored.favorite).toBeNull();
    expect(restored.sort).toBe('name');
    expect(restored.includePeople).toEqual(['p1', 'p2']);
    expect(restored.includePeopleMode).toBe('any');
    expect(restored.excludePeople).toEqual(['p3']);
  });
});

describe('buildFilterChips', () => {
  it('empty query has no chips', () => {
    expect(buildFilterChips(EMPTY_GALLERY_QUERY)).toEqual([]);
    expect(hasActiveQuery(EMPTY_GALLERY_QUERY)).toBe(false);
  });

  it('every supported active filter produces exactly one chip of the right kind', () => {
    const q: GalleryQuery = {
      ...EMPTY_GALLERY_QUERY,
      metadataQuery: 'vacation',
      visualQuery: 'beach at sunset',
      includePeople: ['a', 'b'],
      includePeopleMode: 'all',
      excludePeople: ['c'],
      favorite: true,
      minRating: 4,
      hasGps: true,
      collapseDuplicates: true,
      dateTakenFrom: '2024-06-01T00:00:00.000Z',
      similarTo: 'file-1',
    };
    const kinds = buildFilterChips(q).map((c) => c.kind);
    expect(kinds).toEqual([
      'metadata',
      'visual',
      'people-include',
      'people-exclude',
      'date',
      'favorite',
      'min-rating',
      'gps',
      'collapse',
      'similar',
    ]);
    expect(hasActiveQuery(q)).toBe(true);
  });

  it('carries people ids and mode for accessible labelling', () => {
    const chip = buildFilterChips({
      ...EMPTY_GALLERY_QUERY,
      includePeople: ['a', 'b'],
      includePeopleMode: 'any',
    })[0];
    expect(chip.kind).toBe('people-include');
    expect(chip.personIds).toEqual(['a', 'b']);
    expect(chip.peopleMode).toBe('any');
  });
});

describe('clearChip changes only its own field', () => {
  const full: GalleryQuery = {
    ...EMPTY_GALLERY_QUERY,
    metadataQuery: 'vacation',
    visualQuery: 'beach',
    semanticTopK: 300,
    includePeople: ['a'],
    excludePeople: ['b'],
    favorite: true,
    minRating: 4,
    hasGps: true,
    collapseDuplicates: true,
    dateTakenFrom: '2024-06-01T00:00:00.000Z',
    dateTakenTo: '2024-08-31T00:00:00.000Z',
    similarTo: 'file-1',
  };

  it('removing the visual chip clears visual + topK only', () => {
    const next = clearChip(full, 'visual');
    expect(next.visualQuery).toBe('');
    expect(next.semanticTopK).toBe(0);
    expect(next.metadataQuery).toBe('vacation');
    expect(next.favorite).toBe(true);
  });

  it('removing the date chip clears both bounds only', () => {
    const next = clearChip(full, 'date');
    expect(next.dateTakenFrom).toBe('');
    expect(next.dateTakenTo).toBe('');
    expect(next.includePeople).toEqual(['a']);
  });

  it('removing include-people leaves exclude-people intact', () => {
    const next = clearChip(full, 'people-include');
    expect(next.includePeople).toEqual([]);
    expect(next.excludePeople).toEqual(['b']);
  });

  it('removing favorite leaves rating and gps intact', () => {
    const next = clearChip(full, 'favorite');
    expect(next.favorite).toBeNull();
    expect(next.minRating).toBe(4);
    expect(next.hasGps).toBe(true);
  });
});

describe('date input conversion', () => {
  it('date-only input becomes a UTC midnight instant', () => {
    expect(dateInputToIso('2024-06-01')).toBe('2024-06-01T00:00:00.000Z');
  });
  it('empty stays empty and round-trips to empty', () => {
    expect(dateInputToIso('')).toBe('');
    expect(isoToDateInput('')).toBe('');
  });
  it('iso renders back to the date input', () => {
    expect(isoToDateInput('2024-06-01T00:00:00.000Z')).toBe('2024-06-01');
  });
});

describe('queryToCurrentFilterState', () => {
  it('maps metadata + visual to the interpreter contract', () => {
    const state = queryToCurrentFilterState({
      ...EMPTY_GALLERY_QUERY,
      metadataQuery: 'x',
      visualQuery: 'y',
      favorite: false,
    });
    expect(state.metadataSearch).toBe('x');
    expect(state.semanticQuery).toBe('y');
    expect(state.favorite).toBe(false);
    expect(state.peopleMatch).toBe('all');
  });
});

// --- album membership (shared with the video gallery) ---------------------

describe('albumMembership', () => {
  it('defaults to "any" and is then omitted from the wire query', () => {
    expect(EMPTY_GALLERY_QUERY.albumMembership).toBe('any');
    expect(queryToListQuery(EMPTY_GALLERY_QUERY, null).albumMembership).toBeUndefined();
  });

  it('serializes assigned and unassigned onto the wire query', () => {
    for (const value of ['assigned', 'unassigned'] as const) {
      const query: GalleryQuery = { ...EMPTY_GALLERY_QUERY, albumMembership: value };
      expect(queryToListQuery(query, null).albumMembership).toBe(value);
    }
  });

  it('round-trips through the URL (it is a shareable, non-sensitive filter)', () => {
    const query: GalleryQuery = { ...EMPTY_GALLERY_QUERY, albumMembership: 'unassigned' };
    const params = queryToUrlParams(query);
    expect(params.get('albumMembership')).toBe('unassigned');
    expect(queryFromUrlParams(params).albumMembership).toBe('unassigned');
  });

  it('keeps "any" out of the URL and reads a missing/invalid param as "any"', () => {
    expect(queryToUrlParams(EMPTY_GALLERY_QUERY).has('albumMembership')).toBe(false);
    expect(queryFromUrlParams(new URLSearchParams()).albumMembership).toBe('any');
    expect(queryFromUrlParams(new URLSearchParams('albumMembership=bogus')).albumMembership).toBe('any');
  });

  it('produces a removable chip that clears only this filter', () => {
    const query: GalleryQuery = {
      ...EMPTY_GALLERY_QUERY,
      albumMembership: 'assigned',
      favorite: true,
    };

    const chips = buildFilterChips(query);
    const chip = chips.find((c) => c.kind === 'album-membership');
    expect(chip).toBeDefined();
    expect(chip!.albumMembership).toBe('assigned');
    expect(hasActiveQuery(query)).toBe(true);

    const cleared = clearChip(query, 'album-membership');
    expect(cleared.albumMembership).toBe('any');
    // Neighbouring filters are untouched.
    expect(cleared.favorite).toBe(true);
    expect(buildFilterChips(cleared).some((c) => c.kind === 'album-membership')).toBe(false);
  });

  it('is not a chip when set to "any"', () => {
    expect(buildFilterChips(EMPTY_GALLERY_QUERY).some((c) => c.kind === 'album-membership')).toBe(false);
  });

  it('survives an interpreter refine (the NL flow cannot express it yet)', () => {
    const base: GalleryQuery = { ...EMPTY_GALLERY_QUERY, albumMembership: 'unassigned' };

    const refined = applyInterpretDraft(base, draft({ operation: 'refine', favorite: true }));
    expect(refined.albumMembership).toBe('unassigned');

    // `clear` still resets everything, including this filter.
    const cleared = applyInterpretDraft(base, draft({ operation: 'clear' }));
    expect(cleared.albumMembership).toBe('any');
  });
});

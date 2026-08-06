import assert from 'node:assert/strict';
import test from 'node:test';
import {
  buildGalleryQueryString,
  countActiveFilters,
  defaultSort,
  draftSummaryLines,
  draftToFilters,
  emptyFilters,
  galleryGridMetrics,
  hasActiveFilters,
  initialLoadState,
  isValidDateInput,
  pageFailed,
  pageLoaded,
  peopleIds,
  remapFocusIndex,
  removeItem,
  startLoadMore,
  startNewQuery,
  toCurrentFilterState,
  updateItem,
  type GalleryFilters,
  type GalleryLoadState,
  type InterpretDraft,
} from './galleryQuery.ts';

interface Item { id: string; favorite?: boolean }

const page = (ids: string[], nextCursor: string | null, totalCount = ids.length) => ({
  items: ids.map((id) => ({ id })),
  nextCursor,
  totalCount,
});

// ── query-string serialization ──────────────────────────────────────────────

test('empty filters serialize to the full-gallery query (limit + default sort only)', () => {
  const qs = buildGalleryQueryString(emptyFilters, defaultSort, 50, null);
  assert.equal(qs, 'limit=50&sort=created&direction=desc');
});

test('every filter dimension round-trips into the backend field names', () => {
  const filters: GalleryFilters = {
    semanticQuery: 'sunset',
    semanticTopK: 300,
    q: 'mare estate',
    favorite: true,
    minRating: 3,
    hasGps: false,
    dateFrom: '2023-06-01',
    dateTo: '2023-06-30',
    collapseDuplicates: true,
    people: { 'p-1': 'include', 'p-2': 'exclude', 'p-3': 'include' },
    includePeopleMode: 'any',
  };
  const qs = buildGalleryQueryString(filters, { field: 'name', direction: 'asc' }, 25, 'CUR');
  const params = new URLSearchParams(qs);
  assert.equal(params.get('limit'), '25');
  assert.equal(params.get('q'), 'mare estate');
  assert.equal(params.get('sort'), 'name');
  assert.equal(params.get('direction'), 'asc');
  assert.equal(params.get('favorite'), 'true');
  assert.equal(params.get('minRating'), '3');
  assert.equal(params.get('hasGps'), 'false');
  // Date-only TV values expand to the UTC day bounds (same UTC interpretation
  // as the web gallery's datetime inputs).
  assert.equal(params.get('dateTakenFrom'), '2023-06-01T00:00:00Z');
  assert.equal(params.get('dateTakenTo'), '2023-06-30T23:59:59Z');
  assert.equal(params.get('collapseDuplicates'), 'true');
  assert.equal(params.get('includePeople'), 'p-1,p-3');
  assert.equal(params.get('includePeopleMode'), 'any');
  assert.equal(params.get('excludePeople'), 'p-2');
  assert.equal(params.get('semanticQuery'), 'sunset');
  assert.equal(params.get('semanticTopK'), '300');
  assert.equal(params.get('cursor'), 'CUR');
});

test('include mode is only sent alongside includePeople', () => {
  const excludeOnly: GalleryFilters = {
    ...emptyFilters,
    people: { 'p-9': 'exclude' },
    includePeopleMode: 'any',
  };
  const params = new URLSearchParams(
    buildGalleryQueryString(excludeOnly, defaultSort, 50, null));
  assert.equal(params.get('excludePeople'), 'p-9');
  assert.equal(params.get('includePeople'), null);
  assert.equal(params.get('includePeopleMode'), null);
});

test('active-filter counting covers every dimension and search', () => {
  assert.equal(countActiveFilters(emptyFilters), 0);
  assert.equal(hasActiveFilters(emptyFilters), false);
  const all: GalleryFilters = {
    semanticQuery: 'sunset',
    semanticTopK: 300,
    q: 'x',
    favorite: false,
    minRating: 2,
    hasGps: true,
    dateFrom: '2024-01-01',
    dateTo: '2024-12-31',
    collapseDuplicates: true,
    people: { a: 'include', b: 'exclude' },
    includePeopleMode: 'all',
  };
  assert.equal(countActiveFilters(all), 9);
  assert.deepEqual(peopleIds(all, 'include'), ['a']);
  assert.deepEqual(peopleIds(all, 'exclude'), ['b']);
});

test('grid metrics fit 720p and 1080p widths inside the overscan-safe area', () => {
  for (const { width, inset } of [
    { width: 1280, inset: 38 },
    { width: 1920, inset: 58 },
  ]) {
    const metrics = galleryGridMetrics(width, inset, 8);
    assert.equal(metrics.columns, 8);
    assert.ok(metrics.tileSize > 0);
    assert.ok(metrics.contentWidth <= width - inset * 2);
    assert.ok(width - inset * 2 - metrics.contentWidth < metrics.columns);
  }
});

test('date validation rejects malformed and impossible dates', () => {
  assert.ok(isValidDateInput('2024-02-29')); // leap year
  assert.ok(!isValidDateInput('2023-02-29'));
  assert.ok(!isValidDateInput('2023-13-01'));
  assert.ok(!isValidDateInput('2023-00-10'));
  assert.ok(!isValidDateInput('2023-1-01'));
  assert.ok(!isValidDateInput('20230101'));
  assert.ok(!isValidDateInput(''));
});

// ── generation-guarded accumulator ──────────────────────────────────────────

test('a fresh page replaces, a next page appends without duplicates', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  assert.equal(state.phase, 'loadingInitial');
  state = pageLoaded(state, 1, page(['a', 'b'], 'c1'), false);
  assert.deepEqual(state.items.map((i) => i.id), ['a', 'b']);
  assert.equal(state.phase, 'ready');

  state = startLoadMore(state);
  assert.equal(state.phase, 'loadingMore');
  // Overlapping boundary row 'b' is de-duplicated.
  state = pageLoaded(state, 1, page(['b', 'c'], null), true);
  assert.deepEqual(state.items.map((i) => i.id), ['a', 'b', 'c']);
  assert.equal(state.phase, 'end');
});

test('stale responses (older generation) are ignored entirely', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageLoaded(state, 1, page(['a'], 'c1'), false);
  // A new query starts (generation 2) while page 2 of generation 1 is in flight.
  state = startNewQuery(2);
  const afterStale = pageLoaded(state, 1, page(['zombie'], null), true);
  assert.equal(afterStale, state); // untouched
  const afterStaleFailure = pageFailed(state, 1, true);
  assert.equal(afterStaleFailure, state);
  // The current generation still applies normally.
  state = pageLoaded(state, 2, page(['fresh'], null), false);
  assert.deepEqual(state.items.map((i) => i.id), ['fresh']);
});

test('load-more is only legal from ready/errorMore with a cursor', () => {
  const initial = initialLoadState<Item>();
  assert.equal(startLoadMore(initial), initial); // loadingInitial → no-op
  const end = pageLoaded(startNewQuery<Item>(1), 1, page(['a'], null), false);
  assert.equal(startLoadMore(end), end); // no cursor → no-op
  const failed = pageFailed(
    pageLoaded(startNewQuery<Item>(2), 2, page(['a'], 'c'), false), 2, true);
  assert.equal(failed.phase, 'errorMore');
  assert.equal(startLoadMore(failed).phase, 'loadingMore'); // retry allowed
});

test('failures keep loaded items and mark the right phase', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageFailed(state, 1, false);
  assert.equal(state.phase, 'errorInitial');

  state = startNewQuery(2);
  state = pageLoaded(state, 2, page(['a'], 'c1'), false);
  state = pageFailed(state, 2, true);
  assert.equal(state.phase, 'errorMore');
  assert.deepEqual(state.items.map((i) => i.id), ['a']); // items retained
});

test('updateItem patches one row in place (favorite reconciliation)', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageLoaded(state, 1, {
    items: [{ id: 'a', favorite: false }, { id: 'b', favorite: false }],
    nextCursor: null,
    totalCount: 2,
  }, false);
  state = updateItem(state, 'b', (it) => ({ ...it, favorite: true }));
  assert.equal(state.items[1].favorite, true);
  assert.equal(state.items[0].favorite, false);
  // Unknown id → untouched state.
  assert.equal(updateItem(state, 'nope', (it) => it), state);
});

// ── server-authoritative total count ────────────────────────────────────────

test('first page sets the server total; it is null until then', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  assert.equal(state.totalCount, null);
  // 50 loaded of 842 matching.
  state = pageLoaded(state, 1, page(Array.from({ length: 50 }, (_, i) => `i${i}`), 'c1', 842), false);
  assert.equal(state.items.length, 50);
  assert.equal(state.totalCount, 842);
});

test('appending a page keeps the total stable (never the loaded count)', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageLoaded(state, 1, page(['a', 'b'], 'c1', 842), false);
  state = startLoadMore(state);
  // The backend returns the same total on page 2; loading it must not change it.
  state = pageLoaded(state, 1, page(['c', 'd'], 'c2', 842), true);
  assert.equal(state.totalCount, 842);
  // Loaded items stay a prefix in order, so the loaded index IS the absolute
  // viewer position (index 2 → position 3 of 842).
  assert.deepEqual(state.items.map((i) => i.id), ['a', 'b', 'c', 'd']);
});

test('a filter change resets the total to null and the next page replaces it', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageLoaded(state, 1, page(['a'], null, 842), false);
  assert.equal(state.totalCount, 842);
  // New query identity (filter/search/sort change) → total cleared.
  state = startNewQuery(2);
  assert.equal(state.totalCount, null);
  state = pageLoaded(state, 2, page(['x'], null, 3), false);
  assert.equal(state.totalCount, 3);
});

test('a stale page cannot replace a newer query total', () => {
  let state: GalleryLoadState<Item> = startNewQuery(2);
  state = pageLoaded(state, 2, page(['fresh'], null, 5), false);
  // Page from an older generation (1) arrives late — ignored, total untouched.
  const after = pageLoaded(state, 1, page(['stale'], null, 999), false);
  assert.equal(after, state);
  assert.equal(after.totalCount, 5);
});

test('zero results carry a total of 0', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageLoaded(state, 1, page([], null, 0), false);
  assert.equal(state.items.length, 0);
  assert.equal(state.totalCount, 0);
});

test('removeItem drops a row and decrements the total (membership mutation)', () => {
  let state: GalleryLoadState<Item> = startNewQuery(1);
  state = pageLoaded(state, 1, page(['a', 'b', 'c'], 'c1', 10), false);
  // Un-favoriting 'b' while favoritesOnly is active removes it from the set.
  state = removeItem(state, 'b');
  assert.deepEqual(state.items.map((i) => i.id), ['a', 'c']);
  assert.equal(state.totalCount, 9);
  // Cursor/phase untouched — paging continues.
  assert.equal(state.nextCursor, 'c1');
  assert.equal(state.phase, 'ready');
  // Unknown id → no change (same reference).
  assert.equal(removeItem(state, 'nope'), state);
});

test('removeItem floors the total at 0 and tolerates a null total', () => {
  let zero: GalleryLoadState<Item> = startNewQuery(1);
  zero = pageLoaded(zero, 1, page(['only'], null, 0), false);
  // Defensive: a 0 total never goes negative.
  zero = { ...zero, totalCount: 0 };
  zero = removeItem(zero, 'only');
  assert.equal(zero.totalCount, 0);
  assert.deepEqual(zero.items, []);

  // A null total (first page not yet loaded) stays null.
  const pending: GalleryLoadState<Item> = { ...startNewQuery<Item>(2), items: [{ id: 'a' }] };
  const after = removeItem(pending, 'a');
  assert.equal(after.totalCount, null);
  assert.deepEqual(after.items, []);
});

// ── focus remapping ─────────────────────────────────────────────────────────

test('focus is preserved by id across filter transitions where possible', () => {
  const before = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
  const filtered = [{ id: 'c' }, { id: 'b' }];
  // Focused 'b' survives → follow it.
  assert.equal(remapFocusIndex(before, 1, filtered), 1);
  // Focused 'a' disappeared → nearest valid index.
  assert.equal(remapFocusIndex(before, 0, filtered), 0);
  // Out-of-range previous index → clamped to the last item ('c'), which
  // survives in the filtered list → followed by id.
  assert.equal(remapFocusIndex(before, 99, filtered), 0);
  assert.equal(remapFocusIndex([], 0, filtered), 0);
  assert.equal(remapFocusIndex(before, 1, []), 0);
});

// ── natural-language draft mapping (slice 100) ──────────────────────────────

function baseDraft(overrides: Partial<InterpretDraft> = {}): InterpretDraft {
  return {
    version: 1, operation: 'replace', peopleInclude: [], peopleExclude: [], peopleMatch: 'all',
    favorite: null, minRating: null, hasGps: null, dateTakenFrom: null, dateTakenTo: null,
    collapseDuplicates: null, sort: null, sortDirection: null, metadataSearch: null,
    semanticQuery: null, semanticQueryEnglish: null, semanticTopK: 0, ...overrides,
  };
}

test('draftToFilters maps people include/exclude + semantic + favorite', () => {
  const { filters, sort } = draftToFilters(baseDraft({
    peopleInclude: ['p1'], peopleExclude: ['p2'], peopleMatch: 'any',
    favorite: true, semanticQuery: 'mare al tramonto', semanticTopK: 300,
    dateTakenFrom: '2024-06-01T00:00:00Z', dateTakenTo: '2024-08-31T23:59:59Z',
  }));
  assert.equal(filters.people.p1, 'include');
  assert.equal(filters.people.p2, 'exclude');
  assert.equal(filters.includePeopleMode, 'any');
  assert.equal(filters.favorite, true);
  assert.equal(filters.semanticQuery, 'mare al tramonto');
  assert.equal(filters.semanticTopK, 300);
  assert.equal(filters.dateFrom, '2024-06-01');
  assert.equal(filters.dateTo, '2024-08-31');
  assert.equal(sort.field, 'created');
});

test('draftToFilters clear resets to empty', () => {
  const { filters } = draftToFilters(baseDraft({ operation: 'clear', favorite: true }));
  assert.deepEqual(filters, emptyFilters);
});

test('buildGalleryQueryString includes semanticQuery + semanticTopK', () => {
  const qs = buildGalleryQueryString(
    { ...emptyFilters, favorite: true, semanticQuery: 'neve', semanticTopK: 300 },
    defaultSort, 50, null,
  );
  const params = new URLSearchParams(qs);
  assert.equal(params.get('semanticQuery'), 'neve');
  assert.equal(params.get('semanticTopK'), '300');
  assert.equal(params.get('favorite'), 'true');
});

test('draftSummaryLines summarizes the key dimensions', () => {
  const lines = draftSummaryLines(
    baseDraft({ peopleInclude: ['x'], favorite: true, semanticQuery: 'mare', semanticTopK: 300 }),
    ['Anna'], 'it',
  );
  assert.ok(lines.some((l) => l.includes('Anna')));
  assert.ok(lines.includes('Solo preferite'));
  assert.ok(lines.some((l) => l.includes('mare')));
  assert.ok(lines.some((l) => l.includes('Migliori 300')));
});

test('toCurrentFilterState round-trips the applied state', () => {
  const state = toCurrentFilterState(
    { ...emptyFilters, favorite: true, semanticQuery: 'mare', people: { p1: 'include' } },
    defaultSort,
  );
  assert.deepEqual(state.peopleInclude, ['p1']);
  assert.equal(state.favorite, true);
  assert.equal(state.semanticQuery, 'mare');
  assert.equal(state.sort, 'created');
});

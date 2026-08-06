// Pure state/query helpers for the TV Personal Gallery (no I/O, no React) —
// covered by node --test. The BACKEND owns the authoritative filter/sort/search
// semantics (shared GalleryQueryParser); this module only (1) models the TV
// filter UI state, (2) serializes it to the wire query string, and (3) provides
// a generation-guarded page accumulator so stale responses can never clobber a
// newer query.

export type GallerySortField = 'created' | 'name' | 'size' | 'datetaken';
export type GallerySortDirection = 'asc' | 'desc';

export interface GallerySort {
  field: GallerySortField;
  direction: GallerySortDirection;
}

// Web default: created / desc.
export const defaultSort: GallerySort = { field: 'created', direction: 'desc' };

export type PeopleFilterState = 'include' | 'exclude';

export interface GalleryFilters {
  // Submitted search text ('' = none). Same backend q semantics as the web
  // gallery (name + title + description + tags substring).
  q: string;
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  // Date-only bounds, 'YYYY-MM-DD' ('' = none). The TV edits whole days; the
  // wire values expand to the UTC day bounds (see buildGalleryQueryString), the
  // same UTC interpretation the web gallery uses for its datetime inputs.
  dateFrom: string;
  dateTo: string;
  collapseDuplicates: boolean;
  // personId → include/exclude (absent = unfiltered person).
  people: Record<string, PeopleFilterState>;
  includePeopleMode: 'all' | 'any';
  // Slice 100: visual semantic residual ('' = none) + server-clamped Top-K (0 =
  // none). When set the gallery is physical-filter-first + semantic-ranked.
  semanticQuery: string;
  semanticTopK: number;
}

export const emptyFilters: GalleryFilters = {
  q: '',
  favorite: null,
  minRating: null,
  hasGps: null,
  dateFrom: '',
  dateTo: '',
  collapseDuplicates: false,
  people: {},
  includePeopleMode: 'all',
  semanticQuery: '',
  semanticTopK: 0,
};

export function cloneGalleryFilters(filters: GalleryFilters): GalleryFilters {
  return { ...filters, people: { ...filters.people } };
}

export function galleryGridMetrics(
  viewportWidth: number,
  horizontalInset: number,
  gap: number,
): { columns: number; tileSize: number; contentWidth: number } {
  const columns = Math.min(8, Math.max(4, Math.round(viewportWidth / 160)));
  const available = Math.max(0, viewportWidth - 2 * horizontalInset);
  const tileSize = Math.max(1, Math.floor((available - gap * (columns - 1)) / columns));
  return {
    columns,
    tileSize,
    contentWidth: tileSize * columns + gap * (columns - 1),
  };
}

export function peopleIds(
  filters: GalleryFilters, state: PeopleFilterState,
): string[] {
  return Object.entries(filters.people)
    .filter(([, s]) => s === state)
    .map(([id]) => id);
}

export function hasActiveFilters(filters: GalleryFilters): boolean {
  return countActiveFilters(filters) > 0;
}

// Number of ACTIVE filter dimensions (for the "Filtri (n)" summary). Search
// counts too — it narrows the result set exactly like a filter.
export function countActiveFilters(filters: GalleryFilters): number {
  let count = 0;
  if (filters.q !== '') count += 1;
  if (filters.semanticQuery.trim() !== '') count += 1;
  if (filters.favorite !== null) count += 1;
  if (filters.minRating !== null) count += 1;
  if (filters.hasGps !== null) count += 1;
  if (filters.dateFrom !== '' || filters.dateTo !== '') count += 1;
  if (filters.collapseDuplicates) count += 1;
  if (peopleIds(filters, 'include').length > 0) count += 1;
  if (peopleIds(filters, 'exclude').length > 0) count += 1;
  return count;
}

// 'YYYY-MM-DD' shape + real calendar date (no 2023-02-30).
export function isValidDateInput(value: string): boolean {
  const m = /^(\d{4})-(\d{2})-(\d{2})$/.exec(value);
  if (!m) return false;
  const year = Number(m[1]);
  const month = Number(m[2]);
  const day = Number(m[3]);
  if (year < 1000 || month < 1 || month > 12 || day < 1) return false;
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return day <= daysInMonth;
}

// Serialize the applied state to the wire query. Field names/semantics are the
// backend's (shared with /api/images). Date bounds expand to the UTC day range
// so a from/to on the SAME day includes that whole day.
export function buildGalleryQueryString(
  filters: GalleryFilters,
  sort: GallerySort,
  limit: number,
  cursor: string | null,
): string {
  const params = new URLSearchParams();
  params.set('limit', String(limit));
  if (filters.q !== '') params.set('q', filters.q);
  params.set('sort', sort.field);
  params.set('direction', sort.direction);
  if (filters.favorite !== null) params.set('favorite', String(filters.favorite));
  if (filters.minRating !== null) params.set('minRating', String(filters.minRating));
  if (filters.hasGps !== null) params.set('hasGps', String(filters.hasGps));
  if (filters.dateFrom !== '') params.set('dateTakenFrom', `${filters.dateFrom}T00:00:00Z`);
  if (filters.dateTo !== '') params.set('dateTakenTo', `${filters.dateTo}T23:59:59Z`);
  if (filters.collapseDuplicates) params.set('collapseDuplicates', 'true');
  const include = peopleIds(filters, 'include');
  const exclude = peopleIds(filters, 'exclude');
  if (include.length > 0) {
    params.set('includePeople', include.join(','));
    params.set('includePeopleMode', filters.includePeopleMode);
  }
  if (exclude.length > 0) params.set('excludePeople', exclude.join(','));
  if (filters.semanticQuery !== '') {
    params.set('semanticQuery', filters.semanticQuery);
    if (filters.semanticTopK > 0) params.set('semanticTopK', String(filters.semanticTopK));
  }
  if (cursor !== null) params.set('cursor', cursor);
  return params.toString();
}

// ── natural-language interpret contracts + pure mappers ─────────────────────

export interface InterpretDraft {
  version: number;
  operation: 'replace' | 'refine' | 'clear';
  peopleInclude: string[];
  peopleExclude: string[];
  peopleMatch: 'all' | 'any';
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateTakenFrom: string | null;
  dateTakenTo: string | null;
  collapseDuplicates: boolean | null;
  sort: string | null;
  sortDirection: string | null;
  metadataSearch: string | null;
  semanticQuery: string | null;
  semanticQueryEnglish: string | null;
  semanticTopK: number;
}

export interface InterpretResponse {
  draft: InterpretDraft;
  resolvedPeople: { text: string; mode: 'include' | 'exclude'; personId: string; name: string | null }[];
  ambiguities: {
    text: string;
    mode: 'include' | 'exclude';
    candidates: { personId: string; name: string | null; faceCount: number }[];
  }[];
  warnings: string[];
  requiresClarification: boolean;
}

// Maps a validated draft into the applied filters + sort. Pure (node-tested).
// The server already returns the COMPLETE target state (merged with current for
// refine), so this is a straight projection; 'clear' resets to empty.
export function draftToFilters(
  draft: InterpretDraft,
): { filters: GalleryFilters; sort: GallerySort } {
  if (draft.operation === 'clear') {
    return { filters: { ...emptyFilters }, sort: { ...defaultSort } };
  }
  const people: Record<string, PeopleFilterState> = {};
  for (const id of draft.peopleInclude) people[id] = 'include';
  for (const id of draft.peopleExclude) people[id] = 'exclude';
  const day = (iso: string | null): string => (iso ? iso.slice(0, 10) : '');
  const validField = (s: string | null): GallerySortField =>
    s === 'name' || s === 'size' || s === 'datetaken' ? s : 'created';
  const validDir = (s: string | null): GallerySortDirection => (s === 'asc' ? 'asc' : 'desc');
  return {
    filters: {
      q: draft.metadataSearch ?? '',
      favorite: draft.favorite,
      minRating: draft.minRating,
      hasGps: draft.hasGps,
      dateFrom: day(draft.dateTakenFrom),
      dateTo: day(draft.dateTakenTo),
      collapseDuplicates: draft.collapseDuplicates ?? false,
      people,
      includePeopleMode: draft.peopleMatch,
      semanticQuery: draft.semanticQuery ?? '',
      semanticTopK: draft.semanticQuery ? draft.semanticTopK : 0,
    },
    sort: { field: validField(draft.sort), direction: validDir(draft.sortDirection) },
  };
}

// Localized summary lines for the draft-confirmation panel. Pure (node-tested).
export function draftSummaryLines(
  draft: InterpretDraft,
  peopleNames: string[],
  lang: 'it' | 'en',
): string[] {
  const L = (it: string, en: string): string => (lang === 'it' ? it : en);
  if (draft.operation === 'clear') return [L('Azzera tutti i filtri', 'Clear all filters')];
  const lines: string[] = [];
  if (peopleNames.length > 0 || draft.peopleInclude.length > 0 || draft.peopleExclude.length > 0) {
    const join = draft.peopleMatch === 'any' ? L(' o ', ' or ') : L(' e ', ' and ');
    const inc = peopleNames.length > 0 ? peopleNames.join(join) : String(draft.peopleInclude.length);
    let label = `${L('Persone', 'People')}: ${inc}`;
    if (draft.peopleExclude.length > 0) label += ` (${L('senza', 'without')} ${draft.peopleExclude.length})`;
    lines.push(label);
  }
  if (draft.dateTakenFrom || draft.dateTakenTo) {
    lines.push(`${L('Periodo', 'Period')}: ${(draft.dateTakenFrom ?? '…').slice(0, 10)} → ${(draft.dateTakenTo ?? '…').slice(0, 10)}`);
  }
  if (draft.favorite === true) lines.push(L('Solo preferite', 'Favorites only'));
  if (draft.minRating != null) lines.push(`${L('Valutazione', 'Rating')}: ★ ${draft.minRating}+`);
  if (draft.hasGps === true) lines.push(L('Con posizione', 'With location'));
  if (draft.hasGps === false) lines.push(L('Senza posizione', 'Without location'));
  if (draft.collapseDuplicates === true) lines.push(L('Senza duplicati', 'No duplicates'));
  if (draft.metadataSearch) lines.push(`${L('Testo', 'Text')}: ${draft.metadataSearch}`);
  if (draft.semanticQuery) lines.push(`${L('Contenuto', 'Content')}: ${draft.semanticQuery}`);
  if (draft.sort) lines.push(`${L('Ordine', 'Sort')}: ${draft.sort} ${draft.sortDirection ?? ''}`.trim());
  if (draft.semanticQuery && draft.semanticTopK > 0) {
    lines.push(L(`Migliori ${draft.semanticTopK} risultati`, `Best ${draft.semanticTopK} results`));
  }
  if (lines.length === 0) lines.push(L('Tutte le foto', 'All photos'));
  return lines;
}

// Builds the current-filter-state payload for the interpreter (refine/clear).
export function toCurrentFilterState(filters: GalleryFilters, sort: GallerySort): {
  peopleInclude: string[]; peopleExclude: string[]; peopleMatch: 'all' | 'any';
  favorite: boolean | null; minRating: number | null; hasGps: boolean | null;
  dateTakenFrom: string | null; dateTakenTo: string | null; collapseDuplicates: boolean | null;
  sort: string | null; sortDirection: string | null; metadataSearch: string | null; semanticQuery: string | null;
} {
  return {
    peopleInclude: peopleIds(filters, 'include'),
    peopleExclude: peopleIds(filters, 'exclude'),
    peopleMatch: filters.includePeopleMode,
    favorite: filters.favorite,
    minRating: filters.minRating,
    hasGps: filters.hasGps,
    dateTakenFrom: filters.dateFrom ? `${filters.dateFrom}T00:00:00Z` : null,
    dateTakenTo: filters.dateTo ? `${filters.dateTo}T23:59:59Z` : null,
    collapseDuplicates: filters.collapseDuplicates,
    sort: sort.field,
    sortDirection: sort.direction,
    metadataSearch: filters.q !== '' ? filters.q : null,
    semanticQuery: filters.semanticQuery !== '' ? filters.semanticQuery : null,
  };
}

// ── generation-guarded page accumulator ────────────────────────────────────
// Modeled on the web GalleryPage: the (filters + sort) pair is the query
// identity; changing it starts a new GENERATION. A response is applied only if
// its generation is still current — anything else is stale and ignored.

export interface GalleryItemLike {
  id: string;
}

export type GalleryPhase =
  | 'loadingInitial'
  | 'ready'
  | 'loadingMore'
  | 'end'
  | 'errorInitial'
  | 'errorMore';

export interface GalleryLoadState<T extends GalleryItemLike> {
  generation: number;
  items: T[];
  nextCursor: string | null;
  phase: GalleryPhase;
  // Server-authoritative total for the CURRENT query (null until the first
  // page of this generation lands). This is the viewer counter denominator —
  // NEVER items.length. It stays stable while more pages append and is reset
  // (to null) by startNewQuery so a stale total can never outlive its query.
  totalCount: number | null;
}

export function initialLoadState<T extends GalleryItemLike>(): GalleryLoadState<T> {
  return { generation: 0, items: [], nextCursor: null, phase: 'loadingInitial', totalCount: null };
}

// New query identity: clear the accumulator and invalidate every in-flight page.
// The caller supplies the new generation (it owns the monotonic counter, so the
// fetch closure and the state can never disagree about which run is current).
export function startNewQuery<T extends GalleryItemLike>(
  generation: number,
): GalleryLoadState<T> {
  return {
    generation,
    items: [],
    nextCursor: null,
    phase: 'loadingInitial',
    totalCount: null,
  };
}

// A next-page request left for `nextCursor` — only legal from 'ready'/'errorMore'.
export function startLoadMore<T extends GalleryItemLike>(
  state: GalleryLoadState<T>,
): GalleryLoadState<T> {
  if (state.phase !== 'ready' && state.phase !== 'errorMore') return state;
  if (state.nextCursor === null) return state;
  return { ...state, phase: 'loadingMore' };
}

// Apply a fetched page. Stale generations are ignored outright. Appended pages
// de-dupe by id (cursor pages are normally disjoint; this keeps keys stable if
// they ever overlap, matching the web accumulator).
//
// `totalCount` is the server total for this query. The backend returns the same
// value on every page, so the FIRST page of a generation sets it and later
// (append) pages must NOT change it — this keeps the denominator stable while
// paging even if a concurrent mutation on the server shifted the count between
// requests.
export function pageLoaded<T extends GalleryItemLike>(
  state: GalleryLoadState<T>,
  generation: number,
  page: { items: T[]; nextCursor: string | null; totalCount: number },
  append: boolean,
): GalleryLoadState<T> {
  if (generation !== state.generation) return state;
  const items = append
    ? (() => {
        const seen = new Set(state.items.map((it) => it.id));
        return [...state.items, ...page.items.filter((it) => !seen.has(it.id))];
      })()
    : page.items;
  return {
    generation: state.generation,
    items,
    nextCursor: page.nextCursor,
    phase: page.nextCursor !== null ? 'ready' : 'end',
    totalCount: append ? state.totalCount : page.totalCount,
  };
}

export function pageFailed<T extends GalleryItemLike>(
  state: GalleryLoadState<T>,
  generation: number,
  append: boolean,
): GalleryLoadState<T> {
  if (generation !== state.generation) return state;
  return { ...state, phase: append ? 'errorMore' : 'errorInitial' };
}

// Apply a single-item mutation (e.g. favorite) to the accumulator in place.
export function updateItem<T extends GalleryItemLike>(
  state: GalleryLoadState<T>,
  id: string,
  update: (item: T) => T,
): GalleryLoadState<T> {
  const index = state.items.findIndex((it) => it.id === id);
  if (index < 0) return state;
  const items = state.items.slice();
  items[index] = update(items[index]);
  return { ...state, items };
}

// Membership reconciliation: a mutation removed `id` from the CURRENT result
// set (e.g. un-favoriting while favoritesOnly is active). Drop it from the
// loaded accumulator AND decrement the authoritative total (floored at 0) so
// the counter never goes stale. No-op when the id is not loaded. The cursor /
// phase are untouched — paging continues from where it was.
export function removeItem<T extends GalleryItemLike>(
  state: GalleryLoadState<T>,
  id: string,
): GalleryLoadState<T> {
  const index = state.items.findIndex((it) => it.id === id);
  if (index < 0) return state;
  const items = state.items.slice();
  items.splice(index, 1);
  const totalCount = state.totalCount === null ? null : Math.max(0, state.totalCount - 1);
  return { ...state, items, totalCount };
}

// Focus restoration across list transitions: keep the focused item by ID when
// it survives, otherwise fall back to the nearest valid index.
export function remapFocusIndex(
  previousItems: GalleryItemLike[],
  previousIndex: number,
  nextItems: GalleryItemLike[],
): number {
  if (nextItems.length === 0) return 0;
  const clamped = Math.min(Math.max(previousIndex, 0), Math.max(0, previousItems.length - 1));
  const focusedId = previousItems[clamped]?.id;
  if (focusedId !== undefined) {
    const at = nextItems.findIndex((it) => it.id === focusedId);
    if (at >= 0) return at;
  }
  return Math.min(clamped, nextItems.length - 1);
}

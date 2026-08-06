// Unified Gallery query model (workspace redesign).
//
// The old GalleryPage carried a global `searchMode: 'metadata' | 'semantic'`
// toggle that routed the single search box either to the standalone
// /api/images/semantic endpoint OR to listImages. That global mode is gone.
//
// The workspace exposes TWO INDEPENDENT text fields that may be used together:
//   - `metadataQuery`  → wire `q`            (text & metadata search)
//   - `visualQuery`    → wire `semanticQuery` (visual content, physical-filter-
//                        first semantic ranking through the SAME listImages path)
//
// Everything else is a normal physical filter. This module is pure and fully
// unit-tested: components hold `appliedQuery` + `draftQuery` and use these
// helpers to map to/from the wire, the URL, the interpreter draft and the
// active-filter chips. No React, no i18n, no side effects here.

import type {
  AlbumMembership,
  GalleryCurrentFilterState,
  GalleryInterpretDraft,
  ImageSortDirection,
  ImageSortField,
  ListImagesQuery,
  MediaGalleryScope,
} from '@nubarca/api-client';
import type { PeopleMode } from './PeopleFilterPanel';

export const DEFAULT_GALLERY_LIMIT = 50;

// The single query identity. Any change to a non-`limit` field starts a new
// query generation upstream (resets pagination + selection through the existing
// GalleryPage effect). `limit` is constant in practice.
export interface GalleryQuery {
  metadataQuery: string; // '' = none  → wire q
  visualQuery: string; // '' = none  → wire semanticQuery (visual content)
  semanticTopK: number; // 0 when no visualQuery
  sort: ImageSortField;
  direction: ImageSortDirection;
  limit: number;
  favorite: boolean | null;
  minRating: number | null;
  hasGps: boolean | null;
  dateTakenFrom: string; // '' = none, else ISO-8601 UTC instant
  dateTakenTo: string;
  collapseDuplicates: boolean;
  // Album organisation filter, shared with the video gallery: 'any' (no
  // constraint) | 'assigned' (in at least one album) | 'unassigned' (in none).
  albumMembership: AlbumMembership;
  similarTo: string; // '' = none (bridge from the "similar photos" panel)
  includePeople: string[];
  excludePeople: string[];
  includePeopleMode: PeopleMode;
}

export const EMPTY_GALLERY_QUERY: GalleryQuery = {
  metadataQuery: '',
  visualQuery: '',
  semanticTopK: 0,
  sort: 'created',
  direction: 'desc',
  limit: DEFAULT_GALLERY_LIMIT,
  favorite: null,
  minRating: null,
  hasGps: null,
  dateTakenFrom: '',
  dateTakenTo: '',
  collapseDuplicates: false,
  albumMembership: 'any',
  similarTo: '',
  includePeople: [],
  excludePeople: [],
  includePeopleMode: 'all',
};

// True when a visual (semantic) query is active. In that case results are
// relevance-ranked server-side and the manual sort controls must not be shown
// (the backend ignores them on the semantic path).
export function isSemanticActive(query: GalleryQuery): boolean {
  return query.visualQuery.trim().length > 0;
}

// Whether any user-visible filter/search is active (drives the clear-all action
// and the empty-vs-filtered messaging). Equivalent to `buildFilterChips().length > 0`.
export function hasActiveQuery(query: GalleryQuery): boolean {
  return buildFilterChips(query).length > 0;
}

// ---------------------------------------------------------------- wire mapping

// Build the wire query for a given accumulator cursor (null = first page).
// Always goes through listImages — there is no standalone semantic mode.
export function queryToListQuery(
  query: GalleryQuery,
  cursor: string | null,
  scope: MediaGalleryScope = 'active',
): ListImagesQuery {
  const visual = query.visualQuery.trim();
  return {
    mediaScope: scope,
    q: query.metadataQuery.length > 0 ? query.metadataQuery : undefined,
    sort: query.sort,
    direction: query.direction,
    limit: query.limit,
    cursor: cursor ?? undefined,
    favorite: query.favorite ?? undefined,
    minRating: query.minRating ?? undefined,
    hasGps: query.hasGps ?? undefined,
    dateTakenFrom: query.dateTakenFrom.length > 0 ? query.dateTakenFrom : undefined,
    dateTakenTo: query.dateTakenTo.length > 0 ? query.dateTakenTo : undefined,
    collapseDuplicates: query.collapseDuplicates || undefined,
    albumMembership: query.albumMembership !== 'any' ? query.albumMembership : undefined,
    similarTo: query.similarTo.length > 0 ? query.similarTo : undefined,
    includePeople: query.includePeople.length > 0 ? query.includePeople : undefined,
    excludePeople: query.excludePeople.length > 0 ? query.excludePeople : undefined,
    includePeopleMode: query.includePeople.length > 0 ? query.includePeopleMode : undefined,
    semanticQuery: visual.length > 0 ? visual : undefined,
    semanticTopK: visual.length > 0 ? query.semanticTopK : undefined,
  };
}

// The current filter state the interpreter needs for refine/clear commands.
export function queryToCurrentFilterState(query: GalleryQuery): GalleryCurrentFilterState {
  return {
    peopleInclude: query.includePeople,
    peopleExclude: query.excludePeople,
    peopleMatch: query.includePeopleMode,
    favorite: query.favorite,
    minRating: query.minRating,
    hasGps: query.hasGps,
    dateTakenFrom: query.dateTakenFrom.length > 0 ? query.dateTakenFrom : null,
    dateTakenTo: query.dateTakenTo.length > 0 ? query.dateTakenTo : null,
    collapseDuplicates: query.collapseDuplicates,
    sort: query.sort,
    sortDirection: query.direction,
    metadataSearch: query.metadataQuery.length > 0 ? query.metadataQuery : null,
    semanticQuery: query.visualQuery.length > 0 ? query.visualQuery : null,
  };
}

// ---------------------------------------------------------- interpreter draft

function normalizeSort(sort: string | null): ImageSortField {
  return sort === 'name' || sort === 'size' || sort === 'datetaken' ? sort : 'created';
}

// Map a validated NL interpreter draft onto a base query. The server draft is
// the authoritative TARGET state (it was already computed against the current
// filters we sent), so we trust ONLY the draft — nothing is reconstructed from
// the raw command. `clear` resets to empty; `replace`/`refine` adopt the draft.
// `similarTo` is intentionally dropped (the NL flow never sets a similar bridge).
export function applyInterpretDraft(base: GalleryQuery, draft: GalleryInterpretDraft): GalleryQuery {
  if (draft.operation === 'clear') {
    return { ...EMPTY_GALLERY_QUERY, limit: base.limit };
  }
  const visual = (draft.semanticQuery ?? '').trim();
  return {
    metadataQuery: draft.metadataSearch ?? '',
    visualQuery: visual,
    semanticTopK: visual.length > 0 ? draft.semanticTopK : 0,
    sort: normalizeSort(draft.sort),
    direction: draft.sortDirection === 'asc' ? 'asc' : 'desc',
    limit: base.limit,
    favorite: draft.favorite,
    minRating: draft.minRating,
    hasGps: draft.hasGps,
    dateTakenFrom: draft.dateTakenFrom ?? '',
    dateTakenTo: draft.dateTakenTo ?? '',
    collapseDuplicates: draft.collapseDuplicates ?? false,
    // The NL interpreter has no album-membership vocabulary in this slice, so
    // the draft cannot express it. Carry the current value through `refine`
    // instead of silently dropping a filter the user applied by hand.
    albumMembership: base.albumMembership,
    similarTo: '',
    includePeople: [...draft.peopleInclude],
    excludePeople: [...draft.peopleExclude],
    includePeopleMode: draft.peopleMatch,
  };
}

// ------------------------------------------------------------- URL persistence

// Only safe, shareable, owner-private-or-public fields go in the URL. The
// visual (semantic) query is deliberately NOT persisted (owner-private content
// per the AI privacy rules); it lives only in the in-memory session query.
// Discovery filters (favorite/rating/gps/dates/collapse) are also not persisted
// (matches the prior behaviour — they are session-scoped refinements).
export function queryToUrlParams(query: GalleryQuery): URLSearchParams {
  const sp = new URLSearchParams();
  if (query.metadataQuery.length > 0) sp.set('q', query.metadataQuery);
  if (query.sort !== 'created') sp.set('sort', query.sort);
  if (query.direction !== 'desc') sp.set('direction', query.direction);
  if (query.albumMembership !== 'any') sp.set('albumMembership', query.albumMembership);
  if (query.similarTo.length > 0) sp.set('similarTo', query.similarTo);
  if (query.includePeople.length > 0) {
    sp.set('includePeople', query.includePeople.join(','));
    sp.set('includePeopleMode', query.includePeopleMode);
  }
  if (query.excludePeople.length > 0) sp.set('excludePeople', query.excludePeople.join(','));
  return sp;
}

export function queryFromUrlParams(sp: URLSearchParams): GalleryQuery {
  const sort = sp.get('sort');
  const direction = sp.get('direction');
  const people = sp.get('includePeople');
  const excl = sp.get('excludePeople');
  const mode = sp.get('includePeopleMode');
  const membership = sp.get('albumMembership');
  return {
    ...EMPTY_GALLERY_QUERY,
    metadataQuery: sp.get('q') ?? '',
    sort: normalizeSort(sort),
    direction: direction === 'asc' ? 'asc' : 'desc',
    albumMembership: membership === 'assigned' || membership === 'unassigned' ? membership : 'any',
    similarTo: sp.get('similarTo') ?? '',
    includePeople: people ? people.split(',').filter((x) => x.length > 0) : [],
    excludePeople: excl ? excl.split(',').filter((x) => x.length > 0) : [],
    includePeopleMode: mode === 'any' ? 'any' : 'all',
  };
}

// ----------------------------------------------------------------- filter chips

export type FilterChipKind =
  | 'metadata'
  | 'visual'
  | 'people-include'
  | 'people-exclude'
  | 'date'
  | 'favorite'
  | 'min-rating'
  | 'gps'
  | 'collapse'
  | 'album-membership'
  | 'similar';

// A structured, i18n-free descriptor of one active-filter chip. The component
// localizes it (resolving person ids to names) and attaches a remove handler.
// Chips always describe the APPLIED query; removing one clears only its field.
export interface FilterChipDescriptor {
  key: string;
  kind: FilterChipKind;
  personIds?: string[];
  peopleMode?: PeopleMode; // for people-include: all-vs-any affects the label
  favorite?: boolean;
  minRating?: number;
  hasGps?: boolean;
  dateFrom?: string; // ISO UTC or ''
  dateTo?: string;
  albumMembership?: AlbumMembership;
  text?: string; // metadata / visual raw text
}

// Derive the ordered chip list from a query. Pure + deterministic so the chip
// set can be unit-tested field by field.
export function buildFilterChips(query: GalleryQuery): FilterChipDescriptor[] {
  const chips: FilterChipDescriptor[] = [];
  if (query.metadataQuery.length > 0) {
    chips.push({ key: 'metadata', kind: 'metadata', text: query.metadataQuery });
  }
  if (query.visualQuery.trim().length > 0) {
    chips.push({ key: 'visual', kind: 'visual', text: query.visualQuery.trim() });
  }
  if (query.includePeople.length > 0) {
    chips.push({
      key: 'people-include',
      kind: 'people-include',
      personIds: query.includePeople,
      peopleMode: query.includePeopleMode,
    });
  }
  if (query.excludePeople.length > 0) {
    chips.push({ key: 'people-exclude', kind: 'people-exclude', personIds: query.excludePeople });
  }
  if (query.dateTakenFrom.length > 0 || query.dateTakenTo.length > 0) {
    chips.push({ key: 'date', kind: 'date', dateFrom: query.dateTakenFrom, dateTo: query.dateTakenTo });
  }
  if (query.favorite !== null) {
    chips.push({ key: 'favorite', kind: 'favorite', favorite: query.favorite });
  }
  if (query.minRating !== null) {
    chips.push({ key: 'min-rating', kind: 'min-rating', minRating: query.minRating });
  }
  if (query.hasGps !== null) {
    chips.push({ key: 'gps', kind: 'gps', hasGps: query.hasGps });
  }
  if (query.collapseDuplicates) {
    chips.push({ key: 'collapse', kind: 'collapse' });
  }
  if (query.albumMembership !== 'any') {
    chips.push({
      key: 'album-membership',
      kind: 'album-membership',
      albumMembership: query.albumMembership,
    });
  }
  if (query.similarTo.length > 0) {
    chips.push({ key: 'similar', kind: 'similar' });
  }
  return chips;
}

// Return a new query with the given chip's underlying filter reset to its empty
// value, leaving every other field untouched.
export function clearChip(query: GalleryQuery, kind: FilterChipKind): GalleryQuery {
  switch (kind) {
    case 'metadata':
      return { ...query, metadataQuery: '' };
    case 'visual':
      return { ...query, visualQuery: '', semanticTopK: 0 };
    case 'people-include':
      return { ...query, includePeople: [] };
    case 'people-exclude':
      return { ...query, excludePeople: [] };
    case 'date':
      return { ...query, dateTakenFrom: '', dateTakenTo: '' };
    case 'favorite':
      return { ...query, favorite: null };
    case 'min-rating':
      return { ...query, minRating: null };
    case 'gps':
      return { ...query, hasGps: null };
    case 'collapse':
      return { ...query, collapseDuplicates: false };
    case 'album-membership':
      return { ...query, albumMembership: 'any' };
    case 'similar':
      return { ...query, similarTo: '' };
    default:
      return query;
  }
}

// Convert a `datetime-local`/`date` input value into the stored ISO-UTC instant,
// preserving the existing whole-day/UTC server semantics ("what you see is what
// you send"). Empty string clears the bound.
export function dateInputToIso(value: string): string {
  if (value.length === 0) return '';
  // Accept both "YYYY-MM-DD" (date input) and "YYYY-MM-DDTHH:mm" (datetime-local).
  const withTime = value.length === 10 ? `${value}T00:00` : value;
  return new Date(`${withTime}:00Z`).toISOString();
}

// Render a stored ISO-UTC instant back into a `date` input value (YYYY-MM-DD).
export function isoToDateInput(iso: string): string {
  return iso.length >= 10 ? iso.slice(0, 10) : '';
}

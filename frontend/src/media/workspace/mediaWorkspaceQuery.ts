// Unified media-workspace query model (Slice 5).
//
// This is the SINGLE source of truth that replaces the two independent models
// that used to live side by side:
//   * `frontend/src/gallery/galleryQuery.ts`  → `GalleryQuery`      (photos)
//   * `frontend/src/video/VideoFilterSheet`   → `VideoFilterState`  (videos)
//
// The workspace serves three sources (library, album) × three media kinds
// (all | image | video) × two library scopes (active | excluded) from ONE
// contract. Filters are grouped so the adaptive filter sheet can show exactly
// the controls that are valid for the current kind, and so incompatible
// filters are NEVER sent silently to the backend:
//
//   Common  → valid for every kind (also the ONLY filters shown in "Tutti")
//   Photo   → only valid with kind=image
//   Video   → only valid with kind=video
//
// This module is pure and fully unit-tested: no React, no i18n, no side effects.
// Pages hold `appliedQuery` + `draftQuery` and use these helpers to map to/from
// the wire, the URL, the active-filter chips and the cursor fingerprint.

import type {
  AlbumMembership,
  ImageSortDirection,
  ImageSortField,
  ListMediaQuery,
} from '@nubarca/api-client';

export type PeopleMode = 'all' | 'any';

export const DEFAULT_MEDIA_LIMIT = 50;

// ------------------------------------------------------------------ dimensions

// Which collection the workspace is browsing. `album` carries the album id; the
// backend predicate is the only thing that differs between the two sources.
export type MediaWorkspaceSource =
  | { kind: 'library' }
  | { kind: 'album'; albumId: string };

// Primary in-workspace navigation: the "Tutti | Foto | Video" tabs.
export type MediaKindScope = 'all' | 'image' | 'video';

// "In libreria" (active) vs "Esclusi" (excluded). Personal is a separate,
// security-isolated surface and is never a workspace scope.
export type MediaLibraryScope = 'active' | 'excluded';

// ------------------------------------------------------------------ filter model

// Valid for every media kind. These are the ONLY filters offered in "Tutti".
export interface CommonMediaFilters {
  metadataQuery: string; // '' = none → wire `q` (filename/title/description/tags)
  favorite: boolean | null;
  minRating: number | null;
  dateTakenFrom: string; // '' = none, else ISO-8601 UTC instant
  dateTakenTo: string;
  // Album-membership is a LIBRARY-only concern ('any' | 'assigned' |
  // 'unassigned'). It is meaningless inside a specific album (every item is a
  // member) so the album source drops it entirely — see `queryToWire`.
  albumMembership: AlbumMembership;
}

// Only valid with kind=image. Never sent when kind is 'all' or 'video'.
export interface PhotoMediaFilters {
  visualQuery: string; // '' = none → wire semanticQuery (visual content)
  semanticTopK: number; // 0 when no visualQuery
  hasGps: boolean | null;
  collapseDuplicates: boolean;
  similarTo: string; // '' = none (bridge from the "similar photos" panel)
  includePeople: string[];
  excludePeople: string[];
  includePeopleMode: PeopleMode;
}

// Only valid with kind=video. Never sent when kind is 'all' or 'image'.
// Canonical units: duration in whole seconds, `minHeight` in pixels. The UI
// converts its minute/resolution-preset controls to/from these canonical values.
export interface VideoMediaFilters {
  durationMinSeconds: number | null;
  durationMaxSeconds: number | null;
  minHeight: number | null;
  codec: string; // '' = any
  hasAudio: boolean | null;
}

export interface MediaWorkspaceFilters {
  common: CommonMediaFilters;
  photo: PhotoMediaFilters;
  video: VideoMediaFilters;
}

// The complete query identity. Any change to any field (except `limit`, held on
// the wire query, not here) starts a new query generation upstream: the cursor
// is invalidated, in-flight requests aborted, the accumulator cleared, the
// selection cleared and the viewer closed if necessary.
export interface MediaWorkspaceIdentity {
  source: MediaWorkspaceSource;
  libraryScope: MediaLibraryScope;
  mediaKind: MediaKindScope;
  filters: MediaWorkspaceFilters;
  sort: ImageSortField;
  direction: ImageSortDirection;
}

// -------------------------------------------------------------------- defaults

export const EMPTY_COMMON_FILTERS: CommonMediaFilters = {
  metadataQuery: '',
  favorite: null,
  minRating: null,
  dateTakenFrom: '',
  dateTakenTo: '',
  albumMembership: 'any',
};

export const EMPTY_PHOTO_FILTERS: PhotoMediaFilters = {
  visualQuery: '',
  semanticTopK: 0,
  hasGps: null,
  collapseDuplicates: false,
  similarTo: '',
  includePeople: [],
  excludePeople: [],
  includePeopleMode: 'all',
};

export const EMPTY_VIDEO_FILTERS: VideoMediaFilters = {
  durationMinSeconds: null,
  durationMaxSeconds: null,
  minHeight: null,
  codec: '',
  hasAudio: null,
};

export const EMPTY_MEDIA_FILTERS: MediaWorkspaceFilters = {
  common: EMPTY_COMMON_FILTERS,
  photo: EMPTY_PHOTO_FILTERS,
  video: EMPTY_VIDEO_FILTERS,
};

export function emptyMediaFilters(): MediaWorkspaceFilters {
  return {
    common: { ...EMPTY_COMMON_FILTERS },
    photo: { ...EMPTY_PHOTO_FILTERS, includePeople: [], excludePeople: [] },
    video: { ...EMPTY_VIDEO_FILTERS },
  };
}

export function emptyIdentity(source: MediaWorkspaceSource): MediaWorkspaceIdentity {
  return {
    source,
    libraryScope: 'active',
    mediaKind: 'all',
    filters: emptyMediaFilters(),
    sort: 'created',
    direction: 'desc',
  };
}

// ---------------------------------------------------------------- wire mapping

// The wire shape accepted by the unified endpoints:
//   GET /api/media                    (source = library)
//   GET /api/albums/{albumId}/media   (source = album)
// Photo params are present ONLY when kind=image, video params ONLY when
// kind=video — this mirrors the backend validation (kind=all with a photo/video
// filter is a 400) and guarantees the frontend never applies a hidden,
// incompatible filter.
// True when a visual (semantic) query is set AND actually applies to the
// current tab + source (VSEM-03). The unified /api/media contract deliberately
// does NOT carry a semantic residual (visual, relevance-ranked search keeps its
// own score cursor); the workspace hook branches on this to route visual search
// through a dedicated semantic endpoint instead of listMedia:
//   * photo tab            → /api/images (server-side ranking, honours albumId)
//   * "Tutti" / "Video"    → /api/media/semantic (library-scoped)
// The unified endpoint takes no album parameter, so a visual query cannot apply
// to a non-photo tab inside an album — this predicate is the SINGLE place that
// knows it, so the sheet never offers it, the chips never claim it and the
// fingerprint never keys on it there. `queryToWire` never emits it.
export function isSemanticActive(identity: MediaWorkspaceIdentity): boolean {
  if (identity.filters.photo.visualQuery.trim().length === 0) return false;
  if (identity.mediaKind === 'image') return true;
  return identity.source.kind === 'library';
}

// Build the unified wire query for a given accumulator cursor (null = first
// page). Photo params are emitted ONLY on the image tab and video params ONLY on
// the video tab, so an incompatible filter can never reach the backend.
export function queryToWire(
  identity: MediaWorkspaceIdentity,
  cursor: string | null,
  limit: number = DEFAULT_MEDIA_LIMIT,
): ListMediaQuery {
  const { common, photo, video } = identity.filters;
  const wire: ListMediaQuery = {
    kind: identity.mediaKind,
    scope: identity.libraryScope !== 'active' ? identity.libraryScope : undefined,
    q: common.metadataQuery.length > 0 ? common.metadataQuery : undefined,
    favorite: common.favorite ?? undefined,
    minRating: common.minRating ?? undefined,
    dateTakenFrom: common.dateTakenFrom.length > 0 ? common.dateTakenFrom : undefined,
    dateTakenTo: common.dateTakenTo.length > 0 ? common.dateTakenTo : undefined,
    // album-membership is a library-only filter; the album source has no use
    // for it (all its items are members) and the backend rejects it there.
    albumMembership:
      identity.source.kind === 'library' && common.albumMembership !== 'any'
        ? common.albumMembership
        : undefined,
    sort: identity.sort,
    direction: identity.direction,
    limit,
    cursor: cursor ?? undefined,
  };

  if (identity.mediaKind === 'image') {
    wire.hasGps = photo.hasGps ?? undefined;
    wire.collapseDuplicates = photo.collapseDuplicates || undefined;
    // `similarTo` is deliberately NOT sent to /api/media: the unified endpoint
    // does not resolve a similarity restrict-set. Similar search routes through
    // the dedicated /api/images path instead (see useMediaWorkspace), which
    // resolves it server-side and honours albumId — so it is never an invisible
    // filter here.
    wire.includePeople = photo.includePeople.length > 0 ? photo.includePeople : undefined;
    wire.excludePeople = photo.excludePeople.length > 0 ? photo.excludePeople : undefined;
    wire.includePeopleMode = photo.includePeople.length > 0 ? photo.includePeopleMode : undefined;
  } else if (identity.mediaKind === 'video') {
    wire.durationMin = video.durationMinSeconds ?? undefined;
    wire.durationMax = video.durationMaxSeconds ?? undefined;
    wire.minHeight = video.minHeight ?? undefined;
    wire.codec = video.codec.length > 0 ? video.codec : undefined;
    wire.hasAudio = video.hasAudio ?? undefined;
  }

  return wire;
}

// ---------------------------------------------------------------- fingerprint

// A deterministic string identifying the query (minus the cursor / limit). Two
// identities produce the same fingerprint iff they would page the same result
// set. Used to detect when a cursor must be invalidated and the accumulator
// reset. Owner is server-side and intentionally not part of the client
// fingerprint. Photo filters are folded in ONLY on the image tab and video
// filters ONLY on the video tab, so switching kinds always changes it.
export function queryFingerprint(identity: MediaWorkspaceIdentity): string {
  const { common, photo, video } = identity.filters;
  const source =
    identity.source.kind === 'album' ? `album:${identity.source.albumId}` : 'library';
  const parts: unknown[] = [
    source,
    identity.libraryScope,
    identity.mediaKind,
    common.metadataQuery,
    common.favorite,
    common.minRating,
    common.dateTakenFrom,
    common.dateTakenTo,
    identity.source.kind === 'library' ? common.albumMembership : 'n/a',
    identity.sort,
    identity.direction,
    // VSEM-03: the visual query is a cross-kind dimension — it changes the
    // result set on every tab where it APPLIES (see isSemanticActive), so it
    // is folded in there and nowhere else.
    isSemanticActive(identity) ? photo.visualQuery.trim() : '',
  ];
  if (identity.mediaKind === 'image') {
    parts.push(
      'photo',
      photo.visualQuery.trim().length > 0 ? photo.semanticTopK : 0,
      photo.hasGps,
      photo.collapseDuplicates,
      photo.similarTo,
      [...photo.includePeople].sort().join(','),
      [...photo.excludePeople].sort().join(','),
      photo.includePeople.length > 0 ? photo.includePeopleMode : 'all',
    );
  } else if (identity.mediaKind === 'video') {
    parts.push(
      'video',
      video.durationMinSeconds,
      video.durationMaxSeconds,
      video.minHeight,
      video.codec,
      video.hasAudio,
    );
  }
  return JSON.stringify(parts);
}

// ------------------------------------------------------------- URL persistence

// Only safe, shareable fields go in the URL. Visual (semantic) query, people,
// GPS, similar and the discovery filters (favorite/rating/dates/collapse) are
// session-scoped refinements and deliberately NOT persisted — matching the
// prior galleryQuery behaviour and the owner-private AI rules. `kind` and
// `scope` are carried by the route/search params by the page, not here.
export function filtersToUrlParams(identity: MediaWorkspaceIdentity): URLSearchParams {
  const sp = new URLSearchParams();
  const { common, photo } = identity.filters;
  if (identity.mediaKind !== 'all') sp.set('kind', identity.mediaKind);
  if (identity.libraryScope !== 'active') sp.set('scope', identity.libraryScope);
  if (common.metadataQuery.length > 0) sp.set('q', common.metadataQuery);
  if (identity.sort !== 'created') sp.set('sort', identity.sort);
  if (identity.direction !== 'desc') sp.set('direction', identity.direction);
  if (identity.source.kind === 'library' && common.albumMembership !== 'any') {
    sp.set('albumMembership', common.albumMembership);
  }
  if (identity.mediaKind === 'image') {
    if (photo.similarTo.length > 0) sp.set('similarTo', photo.similarTo);
    if (photo.includePeople.length > 0) {
      sp.set('includePeople', photo.includePeople.join(','));
      sp.set('includePeopleMode', photo.includePeopleMode);
    }
    if (photo.excludePeople.length > 0) sp.set('excludePeople', photo.excludePeople.join(','));
  }
  return sp;
}

export function parseMediaKind(value: string | null): MediaKindScope {
  return value === 'image' || value === 'video' ? value : 'all';
}

export function parseLibraryScope(value: string | null): MediaLibraryScope {
  return value === 'excluded' ? 'excluded' : 'active';
}

function normalizeSort(sort: string | null): ImageSortField {
  return sort === 'name' || sort === 'size' || sort === 'datetaken' ? sort : 'created';
}

// Rebuild an identity from a source + URL params. Only the persisted fields are
// restored; everything else defaults to empty. `kind`/`scope` come from the URL
// too so a deep-linked "?kind=video" lands on the right tab.
export function identityFromUrlParams(
  source: MediaWorkspaceSource,
  sp: URLSearchParams,
): MediaWorkspaceIdentity {
  const kind = parseMediaKind(sp.get('kind'));
  const filters = emptyMediaFilters();
  filters.common.metadataQuery = sp.get('q') ?? '';
  const membership = sp.get('albumMembership');
  filters.common.albumMembership =
    source.kind === 'library' && (membership === 'assigned' || membership === 'unassigned')
      ? membership
      : 'any';
  if (kind === 'image') {
    filters.photo.similarTo = sp.get('similarTo') ?? '';
    const people = sp.get('includePeople');
    filters.photo.includePeople = people ? people.split(',').filter((x) => x.length > 0) : [];
    const excl = sp.get('excludePeople');
    filters.photo.excludePeople = excl ? excl.split(',').filter((x) => x.length > 0) : [];
    filters.photo.includePeopleMode = sp.get('includePeopleMode') === 'any' ? 'any' : 'all';
  }
  return {
    source,
    libraryScope: parseLibraryScope(sp.get('scope')),
    mediaKind: kind,
    filters,
    sort: normalizeSort(sp.get('sort')),
    direction: sp.get('direction') === 'asc' ? 'asc' : 'desc',
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
  | 'similar'
  | 'duration'
  | 'min-height'
  | 'codec'
  | 'has-audio';

// A structured, i18n-free descriptor of one active-filter chip. The component
// localizes it (resolving person ids to names) and attaches a remove handler.
// Chips always describe the APPLIED query; removing one clears only its field.
export interface FilterChipDescriptor {
  key: string;
  kind: FilterChipKind;
  personIds?: string[];
  peopleMode?: PeopleMode;
  favorite?: boolean;
  minRating?: number;
  hasGps?: boolean;
  hasAudio?: boolean;
  dateFrom?: string;
  dateTo?: string;
  albumMembership?: AlbumMembership;
  durationMinSeconds?: number | null;
  durationMaxSeconds?: number | null;
  minHeight?: number;
  text?: string;
}

// Derive the ordered chip list for the current kind. Common chips always; photo
// chips only on the image tab; video chips only on the video tab — so no chip
// can describe a filter that is not actually applied to the visible results.
export function buildFilterChips(identity: MediaWorkspaceIdentity): FilterChipDescriptor[] {
  const { common, photo, video } = identity.filters;
  const chips: FilterChipDescriptor[] = [];

  if (common.metadataQuery.length > 0) {
    chips.push({ key: 'metadata', kind: 'metadata', text: common.metadataQuery });
  }
  // VSEM-03: a chip is shown only where the visual query really applies, so it
  // can never describe an inert filter.
  if (isSemanticActive(identity)) {
    chips.push({ key: 'visual', kind: 'visual', text: photo.visualQuery.trim() });
  }
  if (identity.mediaKind === 'image' && photo.includePeople.length > 0) {
    chips.push({
      key: 'people-include',
      kind: 'people-include',
      personIds: photo.includePeople,
      peopleMode: photo.includePeopleMode,
    });
  }
  if (identity.mediaKind === 'image' && photo.excludePeople.length > 0) {
    chips.push({ key: 'people-exclude', kind: 'people-exclude', personIds: photo.excludePeople });
  }
  if (common.dateTakenFrom.length > 0 || common.dateTakenTo.length > 0) {
    chips.push({
      key: 'date',
      kind: 'date',
      dateFrom: common.dateTakenFrom,
      dateTo: common.dateTakenTo,
    });
  }
  if (common.favorite !== null) {
    chips.push({ key: 'favorite', kind: 'favorite', favorite: common.favorite });
  }
  if (common.minRating !== null) {
    chips.push({ key: 'min-rating', kind: 'min-rating', minRating: common.minRating });
  }
  if (identity.mediaKind === 'image' && photo.hasGps !== null) {
    chips.push({ key: 'gps', kind: 'gps', hasGps: photo.hasGps });
  }
  if (identity.mediaKind === 'image' && photo.collapseDuplicates) {
    chips.push({ key: 'collapse', kind: 'collapse' });
  }
  if (identity.source.kind === 'library' && common.albumMembership !== 'any') {
    chips.push({
      key: 'album-membership',
      kind: 'album-membership',
      albumMembership: common.albumMembership,
    });
  }
  if (identity.mediaKind === 'image' && photo.similarTo.length > 0) {
    chips.push({ key: 'similar', kind: 'similar' });
  }
  if (
    identity.mediaKind === 'video' &&
    (video.durationMinSeconds !== null || video.durationMaxSeconds !== null)
  ) {
    chips.push({
      key: 'duration',
      kind: 'duration',
      durationMinSeconds: video.durationMinSeconds,
      durationMaxSeconds: video.durationMaxSeconds,
    });
  }
  if (identity.mediaKind === 'video' && video.minHeight !== null) {
    chips.push({ key: 'min-height', kind: 'min-height', minHeight: video.minHeight });
  }
  if (identity.mediaKind === 'video' && video.codec.length > 0) {
    chips.push({ key: 'codec', kind: 'codec', text: video.codec });
  }
  if (identity.mediaKind === 'video' && video.hasAudio !== null) {
    chips.push({ key: 'has-audio', kind: 'has-audio', hasAudio: video.hasAudio });
  }
  return chips;
}

// Whether any user-visible filter/search is active for the current kind.
export function hasActiveFilters(identity: MediaWorkspaceIdentity): boolean {
  return buildFilterChips(identity).length > 0;
}

// Return new filters with the given chip's underlying field reset, leaving
// every other field untouched. The result is scoped to the correct filter group
// so clearing a video chip never disturbs the retained photo state.
export function clearChip(
  filters: MediaWorkspaceFilters,
  kind: FilterChipKind,
): MediaWorkspaceFilters {
  const next: MediaWorkspaceFilters = {
    common: { ...filters.common },
    photo: { ...filters.photo },
    video: { ...filters.video },
  };
  switch (kind) {
    case 'metadata':
      next.common.metadataQuery = '';
      break;
    case 'visual':
      next.photo.visualQuery = '';
      next.photo.semanticTopK = 0;
      break;
    case 'people-include':
      next.photo.includePeople = [];
      break;
    case 'people-exclude':
      next.photo.excludePeople = [];
      break;
    case 'date':
      next.common.dateTakenFrom = '';
      next.common.dateTakenTo = '';
      break;
    case 'favorite':
      next.common.favorite = null;
      break;
    case 'min-rating':
      next.common.minRating = null;
      break;
    case 'gps':
      next.photo.hasGps = null;
      break;
    case 'collapse':
      next.photo.collapseDuplicates = false;
      break;
    case 'album-membership':
      next.common.albumMembership = 'any';
      break;
    case 'similar':
      next.photo.similarTo = '';
      break;
    case 'duration':
      next.video.durationMinSeconds = null;
      next.video.durationMaxSeconds = null;
      break;
    case 'min-height':
      next.video.minHeight = null;
      break;
    case 'codec':
      next.video.codec = '';
      break;
    case 'has-audio':
      next.video.hasAudio = null;
      break;
  }
  return next;
}

// Clear every filter that is meaningful for the current kind, preserving the
// retained state of the OTHER kinds' filters (so "Clear all" on the video tab
// does not wipe photo people selections held for when the user returns).
export function clearActiveFilters(identity: MediaWorkspaceIdentity): MediaWorkspaceFilters {
  const next: MediaWorkspaceFilters = {
    common: { ...EMPTY_COMMON_FILTERS },
    photo: { ...identity.filters.photo },
    video: { ...identity.filters.video },
  };
  // Album-membership is only shown/cleared in the library source.
  if (identity.source.kind === 'album') {
    next.common.albumMembership = identity.filters.common.albumMembership;
  }
  if (identity.mediaKind === 'image') {
    next.photo = { ...EMPTY_PHOTO_FILTERS, includePeople: [], excludePeople: [] };
  } else if (identity.mediaKind === 'video') {
    next.video = { ...EMPTY_VIDEO_FILTERS };
  }
  // VSEM-03: the visual query is active (and its chip visible) on every tab,
  // so "clear all" always clears it — while the OTHER retained photo filters
  // still survive a video-tab clear.
  if (identity.mediaKind !== 'image') {
    next.photo = { ...next.photo, visualQuery: '', semanticTopK: 0 };
  }
  return next;
}

// ----------------------------------------------------------------- date helpers

// Convert a `datetime-local`/`date` input value into the stored ISO-UTC instant,
// preserving whole-day/UTC server semantics ("what you see is what you send").
export function dateInputToIso(value: string): string {
  if (value.length === 0) return '';
  const withTime = value.length === 10 ? `${value}T00:00` : value;
  return new Date(`${withTime}:00Z`).toISOString();
}

// Render a stored ISO-UTC instant back into a `date` input value (YYYY-MM-DD).
export function isoToDateInput(iso: string): string {
  return iso.length >= 10 ? iso.slice(0, 10) : '';
}

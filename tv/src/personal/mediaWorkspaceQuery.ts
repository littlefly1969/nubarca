// Unified TV media-workspace query model — pure, node-testable, no React.
//
// This is the TV counterpart of frontend/src/media/workspace/mediaWorkspaceQuery.ts
// and is a DELIBERATE mirror of it, field for field and default for default. The
// television previously carried its own `galleryQuery` model, built around the
// retired photo-only draft: it had no video filters at all, no album membership,
// and its own idea of what a filter meant. That is what let the TV and the web
// disagree about the same library.
//
// The two files cannot be merged — the TV app is a separate package with no
// build link to the web workspace — so the safety net is not shared code but a
// shared SERVER. `queryToWire` below produces the parameter names of the SAME
// unified endpoints (`/api/tv/personal/media`, `/api/tv/personal/albums/{id}/media`)
// that bind through the SAME MediaCollectionQueryBinder as the web's
// `/api/media`. The backend is the single validator; anything the two clients
// could disagree about is something the server would reject.
//
// The grouping is the web's, and it is not cosmetic:
//   Common → valid for every kind (and the ONLY filters offered in "Tutti")
//   Photo  → only valid with kind=image
//   Video  → only valid with kind=video
// Photo params are emitted ONLY on the Photos tab and video params ONLY on the
// Videos tab, mirroring the backend rule that a photo filter under kind=all is a
// 400. A filter can therefore never be applied invisibly to a tab that does not
// show it.

export type MediaKindScope = 'all' | 'image' | 'video';
export type AlbumMembership = 'any' | 'assigned' | 'unassigned';
export type MediaSortField = 'created' | 'name' | 'size' | 'datetaken';
export type MediaSortDirection = 'asc' | 'desc';
export type PeopleMode = 'all' | 'any';

export const DEFAULT_MEDIA_LIMIT = 50;

// Which collection the workspace is browsing. `album` carries the album id; the
// only thing that differs between the two is the endpoint's source predicate.
export type MediaWorkspaceSource =
  | { kind: 'library' }
  | { kind: 'album'; albumId: string };

export interface CommonMediaFilters {
  metadataQuery: string; // '' = none → wire `q`
  // Visual/semantic text search. It lives in COMMON here although the web keeps
  // it under `photo`, and the difference is deliberate: the web's placement is
  // historical (semantic started photo-only), while this file's own stated
  // taxonomy is "Common = valid for every kind" — which is exactly what
  // semantic text search is, All / Photos / Videos alike. Behaviour is NOT
  // divergent: `isSemanticActive` below mirrors the web predicate exactly, and
  // `queryToWire` never emits it, because semantic retrieval is a different
  // endpoint with its own relevance cursor rather than a filter on the
  // structural list.
  //
  // It is emphatically NOT `q`. `q` is metadata substring matching; this is
  // embedding similarity. Overloading one onto the other would make two
  // different retrievals indistinguishable in the UI and in the cursor.
  visualQuery: string;
  semanticTopK: number; // 0 when no visualQuery
  favorite: boolean | null;
  minRating: number | null;
  dateTakenFrom: string; // '' = none, else ISO-8601 UTC instant
  dateTakenTo: string;
  // Library-only. Meaningless inside an album (every item is a member), so the
  // album source drops it and the album endpoint does not even accept it.
  albumMembership: AlbumMembership;
}

export interface PhotoMediaFilters {
  hasGps: boolean | null;
  collapseDuplicates: boolean;
  includePeople: string[];
  excludePeople: string[];
  includePeopleMode: PeopleMode;
}

// Canonical units, matching the web: duration in whole SECONDS and `minHeight`
// in PIXELS. The TV's minute/resolution preset controls convert to and from
// these, exactly as the web sheet does — the wire never carries a preset.
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

// The complete query identity. Any change to any field starts a new query
// generation upstream: the cursor is invalidated, in-flight responses ignored,
// the accumulator cleared and focus remapped.
export interface MediaWorkspaceIdentity {
  source: MediaWorkspaceSource;
  mediaKind: MediaKindScope;
  filters: MediaWorkspaceFilters;
  sort: MediaSortField;
  direction: MediaSortDirection;
}

// Candidate depth for semantic retrieval. The server clamps it; this is the
// TV's request, matching the web default.
export const DEFAULT_SEMANTIC_TOP_K = 200;

export const EMPTY_COMMON_FILTERS: CommonMediaFilters = {
  metadataQuery: '',
  visualQuery: '',
  semanticTopK: 0,
  favorite: null,
  minRating: null,
  dateTakenFrom: '',
  dateTakenTo: '',
  albumMembership: 'any',
};

export const EMPTY_PHOTO_FILTERS: PhotoMediaFilters = {
  hasGps: null,
  collapseDuplicates: false,
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

export function emptyMediaFilters(): MediaWorkspaceFilters {
  return {
    common: { ...EMPTY_COMMON_FILTERS },
    photo: { ...EMPTY_PHOTO_FILTERS, includePeople: [], excludePeople: [] },
    video: { ...EMPTY_VIDEO_FILTERS },
  };
}

// A DRAFT that shares nothing with what it was cloned from. The filter panel
// opens on one of these, every editor writes into it, and only Apply hands it
// back — so a cancelled panel, or an editor the user backed out of, leaves the
// committed query and its results untouched. The array copies are the part that
// matters: a spread of PhotoMediaFilters alone would leave the draft and the
// committed query pointing at the SAME people arrays.
export function cloneMediaFilters(filters: MediaWorkspaceFilters): MediaWorkspaceFilters {
  return {
    common: { ...filters.common },
    photo: {
      ...filters.photo,
      includePeople: [...filters.photo.includePeople],
      excludePeople: [...filters.photo.excludePeople],
    },
    video: { ...filters.video },
  };
}

export function emptyIdentity(source: MediaWorkspaceSource): MediaWorkspaceIdentity {
  return {
    source,
    mediaKind: 'all',
    filters: emptyMediaFilters(),
    sort: 'created',
    direction: 'desc',
  };
}

// ------------------------------------------------------------------- semantic

/**
 * True when the visual query is set AND actually applies to the current tab and
 * source. This mirrors the web's `isSemanticActive` predicate exactly.
 *
 *   Photos              → always, and album-scoped inside an album
 *   Tutti / Video       → library source only
 *
 * The unified semantic route for mixed media is library-scoped and takes no
 * album parameter, so a visual query cannot apply to a non-photo tab inside an
 * album. This predicate is the SINGLE place that knows it — so the filter panel
 * does not offer it there, the chips do not claim it, the fingerprint does not
 * key on it, and no request is ever made that the server would have to reject.
 */
// ONE gate for the whole feature: whether the filter is offered, whether it
// produces a chip, whether it keys the fingerprint and whether it reaches a
// wire are all this one answer. Putting the flag anywhere else lets those
// disagree — which the first attempt did, and the catalog's coupling tests
// caught three ways at once.
//
// It is TRUE now because the retrieval exists: GET /api/tv/personal/media/semantic
// is a thin adapter over MediaSemanticSearchService, the same canonical service
// behind the web's /api/media/semantic. That service takes a MediaKindScope, so
// one ranking answers All, Photos and Videos, and it returns MediaItem — the
// exact type the ordinary TV projection consumes, which is why a semantic card
// is indistinguishable from an ordinary one in the same grid.
//
// The alternative considered and rejected: routing photos through the separate
// photo-gallery semantic service. It returns ImageItem, which carries no
// favorite, rating or takenAt, so semantic photo results would have rendered
// visibly poorer than ordinary ones beside them.
export const SEMANTIC_RETRIEVAL_AVAILABLE = true;

export function isSemanticActive(identity: MediaWorkspaceIdentity): boolean {
  if (!SEMANTIC_RETRIEVAL_AVAILABLE) return false;
  if (identity.filters.common.visualQuery.trim().length === 0) return false;
  if (identity.mediaKind === 'image') return true;
  return identity.source.kind === 'library';
}

/** Which canonical backend path a semantic request must take. */
export type SemanticRoute = 'photo' | 'media';

/**
 * Photos go through the canonical photo/image semantic pipeline (which honours
 * albumId); All and Videos go through the unified MediaSemanticSearchService.
 * Same split as the web — the TV picks the route, it never implements one.
 */
export function semanticRoute(identity: MediaWorkspaceIdentity): SemanticRoute {
  return identity.mediaKind === 'image' ? 'photo' : 'media';
}

// ---------------------------------------------------------------- wire mapping

export type WireQuery = Record<string, string>;

// Build the query string for the current identity + cursor. The emission rules
// ARE the compatibility rules: photo params only on the Photos tab, video params
// only on the Videos tab, albumMembership only on the library source. That is
// the same shape `queryToWire` produces on the web and the same shape the shared
// backend binder accepts.
export function queryToWire(
  identity: MediaWorkspaceIdentity,
  cursor: string | null,
  limit: number = DEFAULT_MEDIA_LIMIT,
): WireQuery {
  const { common, photo, video } = identity.filters;
  const wire: WireQuery = {
    kind: identity.mediaKind,
    sort: identity.sort,
    direction: identity.direction,
    limit: String(limit),
  };
  if (cursor !== null && cursor.length > 0) wire.cursor = cursor;
  if (common.metadataQuery.length > 0) wire.q = common.metadataQuery;
  if (common.favorite !== null) wire.favorite = String(common.favorite);
  if (common.minRating !== null) wire.minRating = String(common.minRating);
  if (common.dateTakenFrom.length > 0) wire.dateTakenFrom = common.dateTakenFrom;
  if (common.dateTakenTo.length > 0) wire.dateTakenTo = common.dateTakenTo;
  if (identity.source.kind === 'library' && common.albumMembership !== 'any') {
    wire.albumMembership = common.albumMembership;
  }

  if (identity.mediaKind === 'image') {
    if (photo.hasGps !== null) wire.hasGps = String(photo.hasGps);
    if (photo.collapseDuplicates) wire.collapseDuplicates = 'true';
    if (photo.includePeople.length > 0) {
      wire.includePeople = photo.includePeople.join(',');
      wire.includePeopleMode = photo.includePeopleMode;
    }
    if (photo.excludePeople.length > 0) wire.excludePeople = photo.excludePeople.join(',');
  } else if (identity.mediaKind === 'video') {
    if (video.durationMinSeconds !== null) wire.durationMin = String(video.durationMinSeconds);
    if (video.durationMaxSeconds !== null) wire.durationMax = String(video.durationMaxSeconds);
    if (video.minHeight !== null) wire.minHeight = String(video.minHeight);
    if (video.codec.length > 0) wire.codec = video.codec;
    if (video.hasAudio !== null) wire.hasAudio = String(video.hasAudio);
  }

  return wire;
}

/**
 * The query string for a SEMANTIC request. Separate from `queryToWire` because
 * it is a different endpoint with a different contract, not a variant of the
 * structural list:
 *
 *   * the text goes in `q` — on the semantic route `q` IS the semantic query;
 *   * only the structural filters the canonical semantic services actually
 *     support are sent (favorite, rating, date range, album membership). The
 *     others are not silently dropped here — the panel does not offer them
 *     alongside an active semantic query, and the route does not accept them,
 *     so there is no parameter to ignore;
 *   * the cursor is the semantic route's own relevance cursor.
 *
 * Like `queryToWire`, this refuses to emit where the filter does not apply —
 * two barriers, not one. An inapplicable identity yields an EMPTY wire, which
 * is not a request.
 *
 * WHICH FILTERS TRAVEL, AND WHY IT DIFFERS BY KIND
 * -----------------------------------------------
 * The two canonical routes honour different things, and this function tells the
 * truth about that rather than sending everything and hoping:
 *
 *   PHOTOS  → the photo pipeline is physical-filter-FIRST, so the metadata
 *             text, GPS, duplicate collapse, People AND the album all shrink
 *             the candidate set before ranking. `albumId` is what makes an
 *             in-album photo search actually album-scoped.
 *   ALL/VIDEO → the unified media route documents that folder/album/people/
 *             GPS/codec/duration are NOT semantic-aware. They are therefore not
 *             emitted, and `tvFilterApplies` does not offer them either, so the
 *             user is never shown a filter marked applied that changes nothing.
 *
 * An earlier version emitted only q/kind/limit for every kind. Photos lost
 * People, GPS and duplicate collapse; videos lost resolution, codec and audio;
 * and inside an album a photo search silently queried the whole library. Each
 * of those rows still rendered as ACTIVE.
 */
export function semanticToWire(
  identity: MediaWorkspaceIdentity,
  cursor: string | null,
  limit: number = DEFAULT_MEDIA_LIMIT,
): WireQuery {
  if (!isSemanticActive(identity)) return {};
  const { common, photo } = identity.filters;
  const wire: WireQuery = {
    q: common.visualQuery.trim(),
    kind: identity.mediaKind,
    limit: String(limit),
    semanticTopK: String(common.semanticTopK),
  };
  if (cursor !== null && cursor.length > 0) wire.cursor = cursor;
  if (common.favorite !== null) wire.favorite = String(common.favorite);
  if (common.minRating !== null) wire.minRating = String(common.minRating);
  if (common.dateTakenFrom.length > 0) wire.dateTakenFrom = common.dateTakenFrom;
  if (common.dateTakenTo.length > 0) wire.dateTakenTo = common.dateTakenTo;

  if (semanticRoute(identity) === 'photo') {
    // The album is the SCOPE, not a filter: without it an in-album search is a
    // library search wearing the album's title.
    if (identity.source.kind === 'album') wire.albumId = identity.source.albumId;
    // `metadataQuery` is deliberately its own parameter. `q` is the visual
    // description that RANKS; this narrows. Sending one as the other would make
    // two different retrievals indistinguishable.
    if (common.metadataQuery.length > 0) wire.metadataQuery = common.metadataQuery;
    if (photo.hasGps !== null) wire.hasGps = String(photo.hasGps);
    if (photo.collapseDuplicates) wire.collapseDuplicates = 'true';
    if (photo.includePeople.length > 0) {
      wire.includePeople = photo.includePeople.join(',');
      wire.includePeopleMode = photo.includePeopleMode;
    }
    if (photo.excludePeople.length > 0) wire.excludePeople = photo.excludePeople.join(',');
  } else if (identity.source.kind === 'library' && common.albumMembership !== 'any') {
    wire.albumMembership = common.albumMembership;
  }
  return wire;
}

// ----------------------------------------------------------------- fingerprint

// Deterministic identity string, minus cursor and limit. Two identities share a
// fingerprint iff they page the same result set. Photo filters fold in ONLY on
// the Photos tab and video filters ONLY on the Videos tab, so switching kind
// always changes it — which is what guarantees a tab switch cannot serve a
// cursor issued under the other tab's filters.
export function queryFingerprint(identity: MediaWorkspaceIdentity): string {
  const { common, photo, video } = identity.filters;
  const parts: unknown[] = [
    identity.source.kind === 'album' ? `album:${identity.source.albumId}` : 'library',
    identity.mediaKind,
    common.metadataQuery,
    // Semantic text changes the RESULT SET wherever it applies, so it must key
    // the fingerprint there — and must not key it where it is inert, or a tab
    // switch would restart a query that returns the same items.
    isSemanticActive(identity) ? common.visualQuery.trim() : '',
    isSemanticActive(identity) ? common.semanticTopK : 0,
    common.favorite,
    common.minRating,
    common.dateTakenFrom,
    common.dateTakenTo,
    identity.source.kind === 'library' ? common.albumMembership : 'n/a',
    identity.sort,
    identity.direction,
  ];
  if (identity.mediaKind === 'image') {
    parts.push(
      'photo',
      photo.hasGps,
      photo.collapseDuplicates,
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

// --------------------------------------------------------------- applied chips

export type FilterChipKind =
  | 'metadata'
  | 'semantic'
  | 'people-include'
  | 'people-exclude'
  | 'date'
  | 'favorite'
  | 'min-rating'
  | 'gps'
  | 'collapse'
  | 'album-membership'
  | 'duration'
  | 'min-height'
  | 'codec'
  | 'has-audio';

export interface FilterChip {
  kind: FilterChipKind;
  // A short, already-localized-by-the-caller value. Kept structured rather than
  // pre-rendered so the screen owns wording and this module stays i18n-free.
  value?: string | number | boolean | null;
  from?: string;
  to?: string;
  count?: number;
}

// The chips for the CURRENT kind. A chip is shown only where its filter is
// actually applied, so the summary can never claim a filter the results do not
// have — the same rule as the web's buildFilterChips.
export function buildFilterChips(identity: MediaWorkspaceIdentity): FilterChip[] {
  const { common, photo, video } = identity.filters;
  const chips: FilterChip[] = [];

  // A chip claims a filter is APPLIED to the visible results. While a semantic
  // query is running, only what the active canonical route actually honours may
  // claim that — the photo route takes the metadata text into its candidate
  // set, the unified media route does not.
  const semantic = isSemanticActive(identity);
  const photoRoute = semantic && semanticRoute(identity) === 'photo';
  if (common.metadataQuery.length > 0 && (!semantic || photoRoute)) {
    chips.push({ kind: 'metadata', value: common.metadataQuery });
  }
  // Only where it really applies, so the summary can never claim a filter the
  // visible results do not have.
  if (isSemanticActive(identity)) {
    chips.push({ kind: 'semantic', value: common.visualQuery.trim() });
  }
  if (common.favorite !== null) chips.push({ kind: 'favorite', value: common.favorite });
  if (common.minRating !== null) chips.push({ kind: 'min-rating', value: common.minRating });
  if (common.dateTakenFrom.length > 0 || common.dateTakenTo.length > 0) {
    chips.push({ kind: 'date', from: common.dateTakenFrom, to: common.dateTakenTo });
  }
  if (identity.source.kind === 'library' && common.albumMembership !== 'any') {
    chips.push({ kind: 'album-membership', value: common.albumMembership });
  }

  if (identity.mediaKind === 'image') {
    if (photo.includePeople.length > 0) {
      chips.push({ kind: 'people-include', count: photo.includePeople.length });
    }
    if (photo.excludePeople.length > 0) {
      chips.push({ kind: 'people-exclude', count: photo.excludePeople.length });
    }
    if (photo.hasGps !== null) chips.push({ kind: 'gps', value: photo.hasGps });
    if (photo.collapseDuplicates) chips.push({ kind: 'collapse' });
  } else if (identity.mediaKind === 'video' && !semantic) {
    // The unified media semantic route is not video-metadata aware, so with a
    // visual query running these filters are neither offered nor claimed.
    if (video.durationMinSeconds !== null || video.durationMaxSeconds !== null) {
      chips.push({
        kind: 'duration',
        from: video.durationMinSeconds === null ? '' : String(video.durationMinSeconds),
        to: video.durationMaxSeconds === null ? '' : String(video.durationMaxSeconds),
      });
    }
    if (video.minHeight !== null) chips.push({ kind: 'min-height', value: video.minHeight });
    if (video.codec.length > 0) chips.push({ kind: 'codec', value: video.codec });
    if (video.hasAudio !== null) chips.push({ kind: 'has-audio', value: video.hasAudio });
  }
  return chips;
}

export function activeFilterCount(identity: MediaWorkspaceIdentity): number {
  return buildFilterChips(identity).length;
}

// Clear every filter meaningful for the CURRENT kind, preserving the other
// kinds' retained state — so clearing on the Videos tab does not wipe the people
// selection the user will want back on Photos. Mirrors clearActiveFilters().
export function clearActiveFilters(identity: MediaWorkspaceIdentity): MediaWorkspaceFilters {
  const next: MediaWorkspaceFilters = {
    common: { ...EMPTY_COMMON_FILTERS },
    photo: { ...identity.filters.photo },
    video: { ...identity.filters.video },
  };
  if (identity.source.kind === 'album') {
    // The album source never shows album membership, so "clear" must not
    // silently rewrite a value the user cannot see.
    next.common.albumMembership = identity.filters.common.albumMembership;
  }
  if (identity.mediaKind === 'image') {
    next.photo = { ...EMPTY_PHOTO_FILTERS, includePeople: [], excludePeople: [] };
  } else if (identity.mediaKind === 'video') {
    next.video = { ...EMPTY_VIDEO_FILTERS };
  }
  return next;
}

// Switching tabs keeps the retained per-kind state (so going Photos → Videos →
// Photos does not lose a people selection) and changes ONLY the kind. The
// emission rules above are what make that safe: a retained photo filter is
// simply not sent while the Photos tab is not active, so it cannot become an
// invisible filter on "Tutti".
export function withMediaKind(
  identity: MediaWorkspaceIdentity,
  mediaKind: MediaKindScope,
): MediaWorkspaceIdentity {
  return identity.mediaKind === mediaKind ? identity : { ...identity, mediaKind };
}

// ------------------------------------------------------------------- UI units

// The Videos tab offers minute presets; the wire is seconds. Conversion lives
// here so no screen invents its own.
export const DURATION_PRESET_MINUTES = [1, 5, 15, 30] as const;
export const MIN_HEIGHT_PRESETS = [480, 720, 1080, 2160] as const;

export function minutesToSeconds(minutes: number | null): number | null {
  return minutes === null ? null : Math.round(minutes * 60);
}

export function secondsToMinutes(seconds: number | null): number | null {
  return seconds === null ? null : Math.round(seconds / 60);
}

// The date bounds are stored as ISO-8601 UTC INSTANTS but entered as calendar
// days. These are the web sheet's two conversions, character for character
// (mediaWorkspaceQuery.dateInputToIso / isoToDateInput on the frontend): both
// bounds anchor at midnight UTC, so a TV and a browser that pick the same day
// produce the same instant and therefore the same result set. A TV-local
// interpretation of "that day" would quietly shift the boundary by the set-top
// box's timezone.
export function dateInputToIso(value: string): string {
  if (value.length === 0) return '';
  const withTime = value.length === 10 ? `${value}T00:00` : value;
  return new Date(`${withTime}:00Z`).toISOString();
}

export function isoToDateInput(iso: string): string {
  return iso.length >= 10 ? iso.slice(0, 10) : '';
}

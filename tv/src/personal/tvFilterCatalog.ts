// The TV filter CAPABILITY layer for the unified media workspace.
//
// It answers three questions about the query model and nothing else:
//   * which filters exist on this tab and this source (applicability),
//   * how the remote edits each one (editor kind),
//   * whether each one is currently doing something (active).
//
// It stores NO filter values. `MediaWorkspaceFilters` stays the single source
// of truth and this module is a pure projection of it, exactly like
// `buildFilterChips` — which is why there is no second filter model to keep in
// sync, and why every function here is a plain function of an identity plus a
// draft.
//
// It exists because the television silently lost a filter. The people row was
// hand-written as a read-only summary: SELECT emptied the selection, so the row
// could be CLEARED but never SET, and from a remote "Persone" was a control
// that did nothing. `includePeopleMode` had no row at all, so the TV always
// sent `all`. Nothing flagged either, because the panel WAS the list of filters
// — there was no place for the statement "this filter is shown" to disagree
// with "this filter is operable".
//
// Two rules now make that class of defect a compile error instead of a
// discovery:
//
//   * TV_FILTER_OWNER claims EVERY field of MediaWorkspaceFilters. Adding a
//     domain filter field without deciding which TV row owns it does not build.
//   * every claimed row carries an EDITOR, and TvFilterEditor has no
//     'summary'/'readonly' member. A row the remote cannot operate is not
//     expressible.
//
// `wireKeys` closes the loop in the other direction: the panel's applicability
// rule and `queryToWire`'s emission rule are checked against each other rather
// than trusted to stay parallel.

import {
  isSemanticActive,
  SEMANTIC_RETRIEVAL_AVAILABLE,
  type CommonMediaFilters,
  type MediaWorkspaceFilters,
  type MediaWorkspaceIdentity,
  type PhotoMediaFilters,
  type VideoMediaFilters,
} from './mediaWorkspaceQuery.ts';

// One editable setting as the remote sees it. Several domain fields can share
// one id (a period is two instants; a people filter is two lists plus a mode) —
// that is the point: the row is the unit the user operates, not the field.
export type TvFilterId =
  | 'metadataQuery'
  | 'semanticQuery'
  | 'favorite'
  | 'minRating'
  | 'period'
  | 'albumMembership'
  | 'people'
  | 'hasGps'
  | 'collapseDuplicates'
  | 'durationMin'
  | 'durationMax'
  | 'minHeight'
  | 'codec'
  | 'hasAudio';

// Which tab a row belongs to. Mirrors the query model's grouping, which is the
// backend's: a photo filter under kind=all is a 400, so it is also not shown.
export type TvFilterSection = 'common' | 'photo' | 'video';

// How the remote operates the row. There is deliberately NO read-only member:
//   cycle  → SELECT advances to the next value, in place, on the row itself;
//   text   → SELECT opens the on-screen keyboard (free text);
//   period → SELECT opens the From/To editor (presets + exact dates);
//   people → SELECT opens the lazily loaded person picker.
export type TvFilterEditor = 'cycle' | 'text' | 'period' | 'people';

// How the row reaches the server. Almost every filter is a parameter on the
// structural list endpoint; SEMANTIC retrieval is a different endpoint with its
// own relevance cursor, so `queryToWire` deliberately never emits it. Making
// that a declared property rather than an exception keeps the coupling test
// meaningful: every row must still be provably sendable, just not all through
// the same builder.
export type TvFilterTransport = 'list' | 'semantic';

// Whether a row survives an ACTIVE semantic query, and where.
//
// This exists because the two canonical semantic routes honour different
// filters, and a row that the active route cannot honour must not be OFFERED —
// otherwise the user sets it, sees it marked applied, and the results ignore
// it. That is the "applied but inert" defect this module was built to prevent,
// and semantic reintroduced it once already.
//
//   'always'      the route honours it whatever the kind (favorite, rating,
//                 period, and the semantic row itself)
//   'photo-only'  only the photo pipeline honours it — it is physical-filter-
//                 FIRST, so People/GPS/collapse/metadata text shrink the
//                 candidate set before ranking
//   'never'       no semantic route honours it (the video metadata filters)
export type TvSemanticSupport = 'always' | 'photo-only' | 'never';

// Every field of every filter group, as a type. The `satisfies` below is what
// turns "a new domain filter appeared" into a build failure.
type DomainFilterField =
  | keyof CommonMediaFilters
  | keyof PhotoMediaFilters
  | keyof VideoMediaFilters;

// Which TV row owns each domain field. Exhaustive by construction.
export const TV_FILTER_OWNER = {
  metadataQuery: 'metadataQuery',
  visualQuery: 'semanticQuery',
  semanticTopK: 'semanticQuery',
  favorite: 'favorite',
  minRating: 'minRating',
  dateTakenFrom: 'period',
  dateTakenTo: 'period',
  albumMembership: 'albumMembership',
  hasGps: 'hasGps',
  collapseDuplicates: 'collapseDuplicates',
  includePeople: 'people',
  excludePeople: 'people',
  includePeopleMode: 'people',
  durationMinSeconds: 'durationMin',
  durationMaxSeconds: 'durationMax',
  minHeight: 'minHeight',
  codec: 'codec',
  hasAudio: 'hasAudio',
} as const satisfies Record<DomainFilterField, TvFilterId>;

export interface TvFilterDescriptor {
  readonly id: TvFilterId;
  readonly section: TvFilterSection;
  readonly editor: TvFilterEditor;
  // Album membership is meaningless inside an album (every item is a member)
  // and the album endpoint does not accept the parameter, so the row is
  // library-only rather than shown-and-ignored.
  readonly librarySourceOnly: boolean;
  // Inside an ALBUM this row exists only on the Photos tab. The unified
  // semantic route for mixed media is library-scoped and takes no album
  // parameter, so offering it on Tutti/Video inside an album would be a control
  // that silently searched the wrong scope. Mirrors `isSemanticActive`.
  readonly albumPhotoTabOnly: boolean;
  readonly transport: TvFilterTransport;
  readonly semanticSupport: TvSemanticSupport;
  // The query-string parameters this row can produce. Used to prove the panel's
  // applicability rule and queryToWire's emission rule are the same rule.
  readonly wireKeys: readonly string[];
}

// Display order for the panel, top to bottom. Also the canonical order the
// deterministic focus fallback walks when a focused row disappears.
export const TV_FILTER_DESCRIPTORS = [
  { id: 'metadataQuery', section: 'common', editor: 'text', semanticSupport: 'photo-only', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['q'] },
  { id: 'semanticQuery', section: 'common', editor: 'text', semanticSupport: 'always', albumPhotoTabOnly: true, transport: 'semantic', librarySourceOnly: false, wireKeys: ['q'] },
  { id: 'favorite', section: 'common', editor: 'cycle', semanticSupport: 'always', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['favorite'] },
  { id: 'minRating', section: 'common', editor: 'cycle', semanticSupport: 'always', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['minRating'] },
  { id: 'period', section: 'common', editor: 'period', semanticSupport: 'always', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['dateTakenFrom', 'dateTakenTo'] },
  { id: 'albumMembership', section: 'common', editor: 'cycle', semanticSupport: 'always', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: true, wireKeys: ['albumMembership'] },
  { id: 'people', section: 'photo', editor: 'people', semanticSupport: 'photo-only', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['includePeople', 'excludePeople', 'includePeopleMode'] },
  { id: 'hasGps', section: 'photo', editor: 'cycle', semanticSupport: 'photo-only', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['hasGps'] },
  { id: 'collapseDuplicates', section: 'photo', editor: 'cycle', semanticSupport: 'photo-only', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['collapseDuplicates'] },
  { id: 'durationMin', section: 'video', editor: 'cycle', semanticSupport: 'never', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['durationMin'] },
  { id: 'durationMax', section: 'video', editor: 'cycle', semanticSupport: 'never', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['durationMax'] },
  { id: 'minHeight', section: 'video', editor: 'cycle', semanticSupport: 'never', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['minHeight'] },
  { id: 'codec', section: 'video', editor: 'text', semanticSupport: 'never', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['codec'] },
  { id: 'hasAudio', section: 'video', editor: 'cycle', semanticSupport: 'never', albumPhotoTabOnly: false, transport: 'list', librarySourceOnly: false, wireKeys: ['hasAudio'] },
] as const satisfies readonly TvFilterDescriptor[];

type DescribedFilterId = (typeof TV_FILTER_DESCRIPTORS)[number]['id'];
type AssertNever<T extends never> = T;
// Build-time proof that every TvFilterId has a descriptor — and therefore an
// editor. An id with no row above fails this constraint.
export type EveryTvFilterIdIsDescribed = AssertNever<Exclude<TvFilterId, DescribedFilterId>>;

const ORDER = new Map<TvFilterId, number>(
  TV_FILTER_DESCRIPTORS.map((descriptor, index) => [descriptor.id, index]),
);

// A row as the panel renders it: the descriptor plus whether the DRAFT has it
// doing something right now.
export interface TvFilterRow extends TvFilterDescriptor {
  readonly active: boolean;
}

// Is this filter meaningful for the current tab and source? A filter that is
// not applicable is not rendered at all, so it can never be a dead control;
// queryToWire independently refuses to emit it, so it can never reach the wire
// either. Both barriers, not one.
export function tvFilterApplies(
  descriptor: TvFilterDescriptor,
  identity: MediaWorkspaceIdentity,
): boolean {
  // The semantic row follows the SAME gate as the chips, the wire and the
  // fingerprint — see SEMANTIC_RETRIEVAL_AVAILABLE. A row offered while its
  // retrieval is unavailable is a control that changes nothing the user can
  // see, which is the dead-filter defect this module exists to prevent.
  if (descriptor.id === 'semanticQuery' && !SEMANTIC_RETRIEVAL_AVAILABLE) return false;
  if (descriptor.librarySourceOnly && identity.source.kind !== 'library') return false;
  if (descriptor.albumPhotoTabOnly
      && identity.source.kind === 'album'
      && identity.mediaKind !== 'image') {
    return false;
  }
  // A row the ACTIVE semantic route cannot honour is not offered while that
  // query is running. Hiding it is the honest option: leaving it visible means
  // the user can set a filter, see it marked applied, and watch the results
  // ignore it.
  if (isSemanticActive(identity) && descriptor.semanticSupport !== 'always') {
    if (descriptor.semanticSupport === 'never') return false;
    if (identity.mediaKind !== 'image') return false;
  }
  if (descriptor.section === 'photo') return identity.mediaKind === 'image';
  if (descriptor.section === 'video') return identity.mediaKind === 'video';
  return true;
}

// Is the row doing something? Exhaustive over TvFilterId with no default, so a
// new id cannot be added without deciding what "active" means for it.
export function isTvFilterActive(id: TvFilterId, filters: MediaWorkspaceFilters): boolean {
  const { common, photo, video } = filters;
  switch (id) {
    case 'metadataQuery': return common.metadataQuery.length > 0;
    case 'semanticQuery': return common.visualQuery.trim().length > 0;
    case 'favorite': return common.favorite !== null;
    case 'minRating': return common.minRating !== null;
    case 'period': return common.dateTakenFrom.length > 0 || common.dateTakenTo.length > 0;
    case 'albumMembership': return common.albumMembership !== 'any';
    case 'people': return photo.includePeople.length > 0 || photo.excludePeople.length > 0;
    case 'hasGps': return photo.hasGps !== null;
    case 'collapseDuplicates': return photo.collapseDuplicates;
    case 'durationMin': return video.durationMinSeconds !== null;
    case 'durationMax': return video.durationMaxSeconds !== null;
    case 'minHeight': return video.minHeight !== null;
    case 'codec': return video.codec.length > 0;
    case 'hasAudio': return video.hasAudio !== null;
  }
}

// The rows to render, in display order.
//
// BOTH halves read the DRAFT. This once said "applicability from the COMMITTED
// identity, activity from the draft", which was true while applicability
// depended only on the tab and the source — neither of which can change while
// the panel is open. `semanticSupport` broke that: applicability now depends on
// whether a visual query is present, and the user types that INTO the draft.
//
// Reading applicability from the committed identity therefore left the panel
// showing rows the request would not carry for the whole time between typing a
// visual query and pressing Apply — the "applied but inert" defect again, and
// visibly so, because activeFilterCount reads the draft and had already stopped
// counting them.
//
// `identity` still supplies what the draft cannot change (tab, source), so the
// caller cannot get this wrong by passing the committed identity: the draft's
// filters always win here.
export function tvFilterRows(
  identity: MediaWorkspaceIdentity,
  draft: MediaWorkspaceFilters,
): TvFilterRow[] {
  const drafted: MediaWorkspaceIdentity = { ...identity, filters: draft };
  return TV_FILTER_DESCRIPTORS
    .filter((descriptor) => tvFilterApplies(descriptor, drafted))
    .map((descriptor) => ({ ...descriptor, active: isTvFilterActive(descriptor.id, draft) }));
}

// ------------------------------------------------------------------- focus

// Everything the panel can put the remote on. The static controls exist on
// every tab, which is what makes the fallback chain below always terminate.
export type TvFilterFocusKey =
  | TvFilterId
  | 'sort'
  | 'direction'
  | 'reset'
  | 'apply'
  | 'cancel';

const STATIC_FOCUS_KEYS: readonly TvFilterFocusKey[] = [
  'sort', 'direction', 'reset', 'apply', 'cancel',
];

function isStaticTvFilterFocus(
  key: TvFilterFocusKey,
): key is Exclude<TvFilterFocusKey, TvFilterId> {
  return (STATIC_FOCUS_KEYS as readonly string[]).includes(key);
}

// Where the remote must land, given where it wanted to be and which rows exist
// now. Deterministic and total — it never answers "nowhere", which is the state
// that leaves native focus on the container with no visible ring:
//
//   the same row, when it is still there
//   → the nearest row in canonical order (earlier one on a tie)
//   → the primary action
//
// Called on every render, so a row removed by a tab change, a source change or
// a sanitize cannot strand focus on a target that no longer exists.
export function resolveTvFilterFocus(
  preferred: TvFilterFocusKey | null,
  rows: readonly TvFilterRow[],
): TvFilterFocusKey {
  if (preferred === null) return rows.length === 0 ? 'apply' : rows[0].id;
  // The static controls are on every tab, so a request for one is always
  // honourable and the chain below is only ever walked for rows.
  if (isStaticTvFilterFocus(preferred)) return preferred;
  if (rows.length === 0) return 'apply';
  if (rows.some((row) => row.id === preferred)) return preferred;

  const target = ORDER.get(preferred);
  if (target === undefined) return rows[0].id;
  let best = rows[0];
  let bestDistance = Number.POSITIVE_INFINITY;
  for (const row of rows) {
    const distance = Math.abs((ORDER.get(row.id) ?? 0) - target);
    if (distance < bestDistance) {
      bestDistance = distance;
      best = row;
    }
  }
  return best.id;
}

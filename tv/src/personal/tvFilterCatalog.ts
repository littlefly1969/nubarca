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

// Every field of every filter group, as a type. The `satisfies` below is what
// turns "a new domain filter appeared" into a build failure.
type DomainFilterField =
  | keyof CommonMediaFilters
  | keyof PhotoMediaFilters
  | keyof VideoMediaFilters;

// Which TV row owns each domain field. Exhaustive by construction.
export const TV_FILTER_OWNER = {
  metadataQuery: 'metadataQuery',
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
  // The query-string parameters this row can produce. Used to prove the panel's
  // applicability rule and queryToWire's emission rule are the same rule.
  readonly wireKeys: readonly string[];
}

// Display order for the panel, top to bottom. Also the canonical order the
// deterministic focus fallback walks when a focused row disappears.
export const TV_FILTER_DESCRIPTORS = [
  { id: 'metadataQuery', section: 'common', editor: 'text', librarySourceOnly: false, wireKeys: ['q'] },
  { id: 'favorite', section: 'common', editor: 'cycle', librarySourceOnly: false, wireKeys: ['favorite'] },
  { id: 'minRating', section: 'common', editor: 'cycle', librarySourceOnly: false, wireKeys: ['minRating'] },
  { id: 'period', section: 'common', editor: 'period', librarySourceOnly: false, wireKeys: ['dateTakenFrom', 'dateTakenTo'] },
  { id: 'albumMembership', section: 'common', editor: 'cycle', librarySourceOnly: true, wireKeys: ['albumMembership'] },
  { id: 'people', section: 'photo', editor: 'people', librarySourceOnly: false, wireKeys: ['includePeople', 'excludePeople', 'includePeopleMode'] },
  { id: 'hasGps', section: 'photo', editor: 'cycle', librarySourceOnly: false, wireKeys: ['hasGps'] },
  { id: 'collapseDuplicates', section: 'photo', editor: 'cycle', librarySourceOnly: false, wireKeys: ['collapseDuplicates'] },
  { id: 'durationMin', section: 'video', editor: 'cycle', librarySourceOnly: false, wireKeys: ['durationMin'] },
  { id: 'durationMax', section: 'video', editor: 'cycle', librarySourceOnly: false, wireKeys: ['durationMax'] },
  { id: 'minHeight', section: 'video', editor: 'cycle', librarySourceOnly: false, wireKeys: ['minHeight'] },
  { id: 'codec', section: 'video', editor: 'text', librarySourceOnly: false, wireKeys: ['codec'] },
  { id: 'hasAudio', section: 'video', editor: 'cycle', librarySourceOnly: false, wireKeys: ['hasAudio'] },
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
  if (descriptor.librarySourceOnly && identity.source.kind !== 'library') return false;
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

// The rows to render, in display order: applicability from the COMMITTED
// identity (its tab and source), activity from the DRAFT being edited.
export function tvFilterRows(
  identity: MediaWorkspaceIdentity,
  draft: MediaWorkspaceFilters,
): TvFilterRow[] {
  return TV_FILTER_DESCRIPTORS
    .filter((descriptor) => tvFilterApplies(descriptor, identity))
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

export function isStaticTvFilterFocus(
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

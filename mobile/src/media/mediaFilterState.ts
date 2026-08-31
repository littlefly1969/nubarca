// Mobile filter state, built on the SHARED workspace model.
//
// This file adds nothing to the domain: the filter vocabulary, the wire
// mapping and the query identity all come from @nubarca/contracts, so a
// filter means on the phone exactly what it means in the browser. What lives
// here is the phone's own interaction shape — a draft edited inside a sheet
// and applied on confirm, rather than the web's apply-as-you-type — plus the
// query-generation bookkeeping the list needs.
//
// DRAFT vs APPLIED (§7, §18): the sheet edits a DRAFT. Nothing refetches while
// the user is still choosing, and dismissing the sheet throws the draft away.
// The chips always describe the APPLIED query, so they can never advertise a
// filter the visible results are not actually under.

import {
  buildFilterChips,
  clearActiveFilters,
  clearChip,
  emptyIdentity,
  queryFingerprint,
  queryToWire,
  type FilterChipDescriptor,
  type FilterChipKind,
  type ListMediaQuery,
  type MediaKindScope,
  type MediaWorkspaceFilters,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from '@nubarca/contracts';

export type { FilterChipDescriptor, FilterChipKind };

/** One tab's starting point: a kind, a source, and no filters. */
export function initialIdentity(
  kind: MediaKindScope,
  source: MediaWorkspaceSource = { kind: 'library' },
): MediaWorkspaceIdentity {
  const identity = emptyIdentity(source);
  identity.mediaKind = kind;
  // The phone's library default: newest capture first, matching what the tabs
  // showed before filters existed.
  identity.sort = 'datetaken';
  identity.direction = 'desc';
  return identity;
}

/** A deep-enough copy for the sheet to edit without touching the applied state. */
export function draftFrom(identity: MediaWorkspaceIdentity): MediaWorkspaceIdentity {
  return {
    ...identity,
    filters: {
      common: { ...identity.filters.common },
      photo: {
        ...identity.filters.photo,
        includePeople: [...identity.filters.photo.includePeople],
        excludePeople: [...identity.filters.photo.excludePeople],
      },
      video: { ...identity.filters.video },
    },
  };
}

/**
 * The query generation key (§19).
 *
 * Two identities share a generation exactly when they would page the same
 * result set. A change means: cancel what is in flight, drop the cursor, clear
 * the accumulator, and drop a selection that may no longer be in the results.
 * The CURSOR is deliberately not part of it, or every page would look like a
 * new query and the list would reset itself forever.
 */
export function generationOf(identity: MediaWorkspaceIdentity): string {
  return queryFingerprint(identity);
}

/** The wire query for one page. */
export function pageQuery(
  identity: MediaWorkspaceIdentity,
  cursor: string | null,
  limit: number,
): ListMediaQuery {
  return queryToWire(identity, cursor, limit);
}

/** The chips describing the APPLIED query, in display order. */
export function chipsFor(identity: MediaWorkspaceIdentity): FilterChipDescriptor[] {
  return buildFilterChips(identity);
}

/** Remove one chip's filter, leaving every other one standing. */
export function withChipCleared(
  identity: MediaWorkspaceIdentity,
  kind: FilterChipKind,
): MediaWorkspaceIdentity {
  return { ...identity, filters: clearChip(identity.filters, kind) };
}

/** Clear everything meaningful for the current kind. */
export function withFiltersCleared(identity: MediaWorkspaceIdentity): MediaWorkspaceIdentity {
  return { ...identity, filters: clearActiveFilters(identity) };
}

/** Replace the filter block wholesale (what the sheet's "Apply" does). */
export function withFilters(
  identity: MediaWorkspaceIdentity,
  filters: MediaWorkspaceFilters,
): MediaWorkspaceIdentity {
  return { ...identity, filters };
}

// ── People selection helpers (§13) ──────────────────────────────────────────
// A person is added to one side and removed from the other: "with Mario" and
// "without Mario" are contradictory, and letting both stand would send a query
// that can never match anything.

export function togglePerson(
  filters: MediaWorkspaceFilters,
  personId: string,
  side: 'include' | 'exclude',
): MediaWorkspaceFilters {
  const include = new Set(filters.photo.includePeople);
  const exclude = new Set(filters.photo.excludePeople);
  const target = side === 'include' ? include : exclude;
  const other = side === 'include' ? exclude : include;
  if (target.has(personId)) {
    target.delete(personId);
  } else {
    target.add(personId);
    other.delete(personId);
  }
  return {
    ...filters,
    photo: {
      ...filters.photo,
      includePeople: [...include],
      excludePeople: [...exclude],
    },
  };
}

export function personSide(
  filters: MediaWorkspaceFilters,
  personId: string,
): 'include' | 'exclude' | null {
  if (filters.photo.includePeople.includes(personId)) return 'include';
  if (filters.photo.excludePeople.includes(personId)) return 'exclude';
  return null;
}

export function withPeopleMode(
  filters: MediaWorkspaceFilters,
  mode: 'all' | 'any',
): MediaWorkspaceFilters {
  return { ...filters, photo: { ...filters.photo, includePeopleMode: mode } };
}

/** Every person id referenced by the filters, for resolving labels. */
export function referencedPersonIds(filters: MediaWorkspaceFilters): string[] {
  return [...filters.photo.includePeople, ...filters.photo.excludePeople];
}

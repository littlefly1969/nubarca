// Web-side workspace query helpers.
//
// The MODEL — filters, defaults, wire mapping, fingerprint, chips, clearing —
// is the canonical one in @nubarca/contracts, shared with the phone. Only URL
// persistence stays here: it is genuinely web-specific (a phone has no address
// bar) and it needs URLSearchParams, which the neutral package must not touch.
//
// Everything is re-exported so existing web imports of this module are
// unchanged.

import {
  emptyMediaFilters,
  type MediaKindScope,
  type MediaLibraryScope,
  type MediaSortField,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from '@nubarca/contracts';

export * from '@nubarca/contracts';

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

function normalizeSort(sort: string | null): MediaSortField {
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


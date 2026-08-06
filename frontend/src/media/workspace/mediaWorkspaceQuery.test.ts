import { describe, expect, it } from 'vitest';
import {
  buildFilterChips,
  clearActiveFilters,
  clearChip,
  dateInputToIso,
  emptyIdentity,
  emptyMediaFilters,
  filtersToUrlParams,
  hasActiveFilters,
  identityFromUrlParams,
  isSemanticActive,
  isoToDateInput,
  queryFingerprint,
  queryToWire,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from './mediaWorkspaceQuery';

const library: MediaWorkspaceSource = { kind: 'library' };
const album: MediaWorkspaceSource = { kind: 'album', albumId: 'a-1' };

function base(source: MediaWorkspaceSource = library): MediaWorkspaceIdentity {
  return emptyIdentity(source);
}

describe('queryToWire — kind gating', () => {
  it('sends only common filters in the "all" tab, even when photo/video state is present', () => {
    const id = base();
    id.mediaKind = 'all';
    id.filters.common.metadataQuery = 'beach';
    id.filters.common.favorite = true;
    // Non-common state retained in memory must NOT leak onto the wire.
    id.filters.photo.hasGps = true;
    id.filters.photo.includePeople = ['p1'];
    id.filters.video.codec = 'h264';
    const wire = queryToWire(id, null);
    expect(wire.kind).toBe('all');
    expect(wire.q).toBe('beach');
    expect(wire.favorite).toBe(true);
    expect(wire.hasGps).toBeUndefined();
    expect(wire.includePeople).toBeUndefined();
    expect(wire.codec).toBeUndefined();
  });

  it('sends photo filters only on the image tab', () => {
    const id = base();
    id.mediaKind = 'image';
    id.filters.photo.hasGps = true;
    id.filters.photo.collapseDuplicates = true;
    id.filters.photo.includePeople = ['p1', 'p2'];
    id.filters.photo.includePeopleMode = 'any';
    id.filters.video.codec = 'h264';
    const wire = queryToWire(id, null);
    expect(wire.hasGps).toBe(true);
    expect(wire.collapseDuplicates).toBe(true);
    expect(wire.includePeople).toEqual(['p1', 'p2']);
    expect(wire.includePeopleMode).toBe('any');
    expect(wire.codec).toBeUndefined();
    expect(wire.durationMin).toBeUndefined();
  });

  it('sends video filters only on the video tab', () => {
    const id = base();
    id.mediaKind = 'video';
    id.filters.video.durationMinSeconds = 30;
    id.filters.video.minHeight = 1080;
    id.filters.video.codec = 'hevc';
    id.filters.video.hasAudio = true;
    id.filters.photo.hasGps = true;
    const wire = queryToWire(id, null);
    expect(wire.durationMin).toBe(30);
    expect(wire.minHeight).toBe(1080);
    expect(wire.codec).toBe('hevc');
    expect(wire.hasAudio).toBe(true);
    expect(wire.hasGps).toBeUndefined();
  });

  it('drops album-membership for the album source and keeps it for the library source', () => {
    const lib = base(library);
    lib.filters.common.albumMembership = 'assigned';
    expect(queryToWire(lib, null).albumMembership).toBe('assigned');

    const alb = base(album);
    alb.filters.common.albumMembership = 'assigned';
    expect(queryToWire(alb, null).albumMembership).toBeUndefined();
  });

  it('flags semantic-active on the photo tab but never puts semantic on the unified wire', () => {
    const id = base();
    id.mediaKind = 'image';
    id.filters.photo.visualQuery = 'red car';
    id.filters.photo.semanticTopK = 200;
    const wire = queryToWire(id, null);
    // Visual search routes through the dedicated semantic API, so the unified
    // wire carries no semantic residual — but manual sort is still present.
    expect(isSemanticActive(id)).toBe(true);
    expect(wire.sort).toBe('created');
    expect('semanticQuery' in wire).toBe(false);
  });

  // VSEM-03 extended visual search to every tab: "Tutti" and "Video" route to
  // the unified /api/media/semantic. The unified endpoint is library-scoped,
  // so inside an album those tabs stay non-semantic (the control is not even
  // offered there) rather than silently searching the whole library.
  it('a visual query is semantic-active on the "all"/"video" tabs of the library', () => {
    for (const kind of ['all', 'video'] as const) {
      const id = base(library);
      id.mediaKind = kind;
      id.filters.photo.visualQuery = 'red car';
      expect(isSemanticActive(id)).toBe(true);
      // Still never on the unified listing wire — semantic keeps its own path.
      expect('semanticQuery' in queryToWire(id, null)).toBe(false);
    }
  });

  it('a visual query on the "all"/"video" tabs of an ALBUM is not semantic-active', () => {
    for (const kind of ['all', 'video'] as const) {
      const id = base(album);
      id.mediaKind = kind;
      id.filters.photo.visualQuery = 'red car';
      expect(isSemanticActive(id)).toBe(false);
    }
  });
});

describe('queryFingerprint — cursor invalidation', () => {
  it('differs across kind, scope, source and album', () => {
    const allLib = queryFingerprint(base(library));
    const imgLib = queryFingerprint({ ...base(library), mediaKind: 'image' });
    const vidLib = queryFingerprint({ ...base(library), mediaKind: 'video' });
    const excluded = queryFingerprint({ ...base(library), libraryScope: 'excluded' });
    const albA = queryFingerprint(base(album));
    const albB = queryFingerprint(base({ kind: 'album', albumId: 'a-2' }));
    const set = new Set([allLib, imgLib, vidLib, excluded, albA, albB]);
    expect(set.size).toBe(6);
  });

  it('differs when a common filter changes', () => {
    const a = base();
    const b = base();
    b.filters.common.metadataQuery = 'x';
    expect(queryFingerprint(a)).not.toBe(queryFingerprint(b));
  });

  it('is stable regardless of people ordering', () => {
    const a = { ...base(), mediaKind: 'image' as const };
    a.filters.photo.includePeople = ['p2', 'p1'];
    const b = { ...base(), mediaKind: 'image' as const };
    b.filters.photo.includePeople = ['p1', 'p2'];
    expect(queryFingerprint(a)).toBe(queryFingerprint(b));
  });
});

describe('URL persistence', () => {
  it('round-trips kind, scope, metadata, sort and people', () => {
    const id = base();
    id.mediaKind = 'image';
    id.libraryScope = 'excluded';
    id.filters.common.metadataQuery = 'dog';
    id.sort = 'name';
    id.direction = 'asc';
    id.filters.photo.includePeople = ['p1'];
    id.filters.photo.includePeopleMode = 'any';
    const sp = filtersToUrlParams(id);
    const back = identityFromUrlParams(library, sp);
    expect(back.mediaKind).toBe('image');
    expect(back.libraryScope).toBe('excluded');
    expect(back.filters.common.metadataQuery).toBe('dog');
    expect(back.sort).toBe('name');
    expect(back.direction).toBe('asc');
    expect(back.filters.photo.includePeople).toEqual(['p1']);
    expect(back.filters.photo.includePeopleMode).toBe('any');
  });

  it('does not persist owner-private session filters (visual/gps/favorite)', () => {
    const id = base();
    id.mediaKind = 'image';
    id.filters.photo.visualQuery = 'secret';
    id.filters.photo.hasGps = true;
    id.filters.common.favorite = true;
    const sp = filtersToUrlParams(id);
    expect(sp.get('q')).toBeNull();
    expect(sp.toString()).not.toContain('secret');
    expect(sp.get('favorite')).toBeNull();
  });

  it('ignores album-membership from the URL when the source is an album', () => {
    const sp = new URLSearchParams({ albumMembership: 'assigned' });
    expect(identityFromUrlParams(album, sp).filters.common.albumMembership).toBe('any');
    expect(identityFromUrlParams(library, sp).filters.common.albumMembership).toBe('assigned');
  });
});

describe('filter chips', () => {
  it('shows only common chips in the "all" tab', () => {
    const id = base();
    id.mediaKind = 'all';
    id.filters.common.favorite = true;
    id.filters.photo.hasGps = true; // retained but not applicable in "all"
    id.filters.video.codec = 'h264';
    const kinds = buildFilterChips(id).map((c) => c.kind);
    expect(kinds).toContain('favorite');
    expect(kinds).not.toContain('gps');
    expect(kinds).not.toContain('codec');
  });

  it('clearChip only touches the targeted field', () => {
    const id = base();
    id.mediaKind = 'image';
    id.filters.common.favorite = true;
    id.filters.photo.hasGps = true;
    const next = clearChip(id.filters, 'gps');
    expect(next.photo.hasGps).toBeNull();
    expect(next.common.favorite).toBe(true);
  });

  it('clearActiveFilters keeps the other kinds’ retained state', () => {
    const id = base();
    id.mediaKind = 'video';
    id.filters.video.codec = 'h264';
    id.filters.photo.includePeople = ['p1']; // retained for the photo tab
    const cleared = clearActiveFilters(id);
    expect(cleared.video.codec).toBe('');
    expect(cleared.photo.includePeople).toEqual(['p1']);
  });

  it('hasActiveFilters reflects only the current kind', () => {
    const id = base();
    id.mediaKind = 'all';
    id.filters.video.codec = 'h264';
    expect(hasActiveFilters(id)).toBe(false);
    id.mediaKind = 'video';
    expect(hasActiveFilters(id)).toBe(true);
  });
});

describe('date helpers', () => {
  it('round-trips a date input through the ISO instant', () => {
    const iso = dateInputToIso('2026-07-22');
    expect(iso).toBe('2026-07-22T00:00:00.000Z');
    expect(isoToDateInput(iso)).toBe('2026-07-22');
  });

  it('treats an empty input as no bound', () => {
    expect(dateInputToIso('')).toBe('');
    expect(isoToDateInput('')).toBe('');
  });
});

describe('emptyMediaFilters', () => {
  it('produces independent array instances', () => {
    const a = emptyMediaFilters();
    const b = emptyMediaFilters();
    a.photo.includePeople.push('x');
    expect(b.photo.includePeople).toEqual([]);
  });
});

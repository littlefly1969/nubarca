import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  activeFilterCount,
  buildFilterChips,
  clearActiveFilters,
  emptyIdentity,
  minutesToSeconds,
  queryFingerprint,
  queryToWire,
  secondsToMinutes,
  withMediaKind,
  type MediaWorkspaceIdentity,
} from './mediaWorkspaceQuery.ts';

const library = () => emptyIdentity({ kind: 'library' });
const album = () => emptyIdentity({ kind: 'album', albumId: 'a-1' });

function withFilters(
  base: MediaWorkspaceIdentity,
  mutate: (i: MediaWorkspaceIdentity) => void,
): MediaWorkspaceIdentity {
  const next: MediaWorkspaceIdentity = {
    ...base,
    filters: {
      common: { ...base.filters.common },
      photo: { ...base.filters.photo },
      video: { ...base.filters.video },
    },
  };
  mutate(next);
  return next;
}

// ------------------------------------------------------------------ defaults

test('the default identity matches the web workspace defaults exactly', () => {
  const identity = library();
  assert.equal(identity.mediaKind, 'all');
  assert.equal(identity.sort, 'created');
  assert.equal(identity.direction, 'desc');
  assert.equal(identity.filters.common.albumMembership, 'any');
  assert.equal(identity.filters.common.favorite, null);
  assert.equal(identity.filters.common.minRating, null);
  assert.equal(identity.filters.photo.collapseDuplicates, false);
  assert.equal(identity.filters.photo.includePeopleMode, 'all');
  assert.equal(identity.filters.video.codec, '');
  assert.equal(activeFilterCount(identity), 0);
});

test('the default wire query carries only kind, sort, direction and limit', () => {
  assert.deepEqual(queryToWire(library(), null), {
    kind: 'all',
    sort: 'created',
    direction: 'desc',
    limit: '50',
  });
});

// ------------------------------------------------- the parity matrix (task 50)

test('All: only common filters reach the wire', () => {
  // Every group is populated; the "Tutti" tab must emit the common ones and
  // NOTHING else, matching the backend rule that a photo or video filter under
  // kind=all is a 400.
  const identity = withFilters(library(), (i) => {
    i.filters.common.metadataQuery = 'vacanza';
    i.filters.common.favorite = true;
    i.filters.common.minRating = 3;
    i.filters.common.dateTakenFrom = '2026-01-01T00:00:00.000Z';
    i.filters.common.dateTakenTo = '2026-06-30T00:00:00.000Z';
    i.filters.common.albumMembership = 'unassigned';
    i.filters.photo.hasGps = true;
    i.filters.photo.collapseDuplicates = true;
    i.filters.photo.includePeople = ['p1'];
    i.filters.video.minHeight = 1080;
    i.filters.video.codec = 'h264';
  });
  const wire = queryToWire(identity, null);
  assert.deepEqual(wire, {
    kind: 'all',
    sort: 'created',
    direction: 'desc',
    limit: '50',
    q: 'vacanza',
    favorite: 'true',
    minRating: '3',
    dateTakenFrom: '2026-01-01T00:00:00.000Z',
    dateTakenTo: '2026-06-30T00:00:00.000Z',
    albumMembership: 'unassigned',
  });
});

test('Photos: common + photo filters, never video ones', () => {
  const identity = withMediaKind(
    withFilters(library(), (i) => {
      i.filters.common.favorite = false;
      i.filters.photo.hasGps = false;
      i.filters.photo.collapseDuplicates = true;
      i.filters.photo.includePeople = ['p1', 'p2'];
      i.filters.photo.excludePeople = ['p3'];
      i.filters.photo.includePeopleMode = 'any';
      i.filters.video.codec = 'hevc';
      i.filters.video.hasAudio = true;
    }),
    'image',
  );
  const wire = queryToWire(identity, null);
  assert.equal(wire.hasGps, 'false');
  assert.equal(wire.collapseDuplicates, 'true');
  assert.equal(wire.includePeople, 'p1,p2');
  assert.equal(wire.excludePeople, 'p3');
  assert.equal(wire.includePeopleMode, 'any');
  assert.equal(wire.favorite, 'false');
  for (const key of ['codec', 'hasAudio', 'durationMin', 'durationMax', 'minHeight']) {
    assert.equal(wire[key], undefined, `${key} must not reach the Photos tab`);
  }
});

test('Videos: common + video filters, never photo ones', () => {
  const identity = withMediaKind(
    withFilters(library(), (i) => {
      i.filters.common.minRating = 4;
      i.filters.video.durationMinSeconds = 60;
      i.filters.video.durationMaxSeconds = 900;
      i.filters.video.minHeight = 720;
      i.filters.video.codec = 'h264';
      i.filters.video.hasAudio = false;
      i.filters.photo.hasGps = true;
      i.filters.photo.includePeople = ['p1'];
    }),
    'video',
  );
  const wire = queryToWire(identity, null);
  assert.equal(wire.durationMin, '60');
  assert.equal(wire.durationMax, '900');
  assert.equal(wire.minHeight, '720');
  assert.equal(wire.codec, 'h264');
  assert.equal(wire.hasAudio, 'false');
  assert.equal(wire.minRating, '4');
  for (const key of ['hasGps', 'collapseDuplicates', 'includePeople', 'excludePeople']) {
    assert.equal(wire[key], undefined, `${key} must not reach the Videos tab`);
  }
});

test('Library allows album membership; an album never sends it', () => {
  const inLibrary = withFilters(library(), (i) => {
    i.filters.common.albumMembership = 'assigned';
  });
  assert.equal(queryToWire(inLibrary, null).albumMembership, 'assigned');

  // Inside an album every item is a member, so the filter is meaningless. The
  // backend rejects it there and the TV album endpoint does not even accept the
  // parameter — so it must never be emitted.
  const inAlbum = withFilters(album(), (i) => {
    i.filters.common.albumMembership = 'assigned';
  });
  assert.equal(queryToWire(inAlbum, null).albumMembership, undefined);
});

test('album membership never appears as a chip inside an album', () => {
  const inAlbum = withFilters(album(), (i) => {
    i.filters.common.albumMembership = 'unassigned';
  });
  assert.ok(!buildFilterChips(inAlbum).some((c) => c.kind === 'album-membership'));
});

// ---------------------------------------------------- no invisible filters

test('switching tabs never carries a hidden filter into the results', () => {
  // The reported product risk: a photo filter set on Photos silently narrowing
  // "Tutti". Retention is fine — EMISSION is what matters.
  const onPhotos = withMediaKind(
    withFilters(library(), (i) => {
      i.filters.photo.hasGps = true;
      i.filters.photo.includePeople = ['p1'];
    }),
    'image',
  );
  const onAll = withMediaKind(onPhotos, 'all');
  const wire = queryToWire(onAll, null);
  assert.equal(wire.hasGps, undefined);
  assert.equal(wire.includePeople, undefined);
  // The state is retained so returning to Photos restores the selection...
  assert.deepEqual(onAll.filters.photo.includePeople, ['p1']);
  // ...and the chips do not claim a filter the visible results do not have.
  assert.equal(activeFilterCount(onAll), 0);
  assert.equal(activeFilterCount(withMediaKind(onAll, 'image')), 2);
});

test('the fingerprint changes on every tab switch, so a cursor cannot cross tabs', () => {
  const base = withFilters(library(), (i) => { i.filters.photo.hasGps = true; });
  const all = queryFingerprint(withMediaKind(base, 'all'));
  const photos = queryFingerprint(withMediaKind(base, 'image'));
  const videos = queryFingerprint(withMediaKind(base, 'video'));
  assert.notEqual(all, photos);
  assert.notEqual(all, videos);
  assert.notEqual(photos, videos);
});

test('the fingerprint ignores retained filters of the inactive kinds', () => {
  const plain = withMediaKind(library(), 'video');
  const withRetainedPhoto = withMediaKind(
    withFilters(library(), (i) => {
      i.filters.photo.hasGps = true;
      i.filters.photo.collapseDuplicates = true;
    }),
    'video',
  );
  assert.equal(queryFingerprint(plain), queryFingerprint(withRetainedPhoto),
    'a retained photo filter must not invalidate a Videos cursor it never reached');
});

test('people order does not change the query identity', () => {
  const a = withMediaKind(withFilters(library(), (i) => {
    i.filters.photo.includePeople = ['p2', 'p1'];
  }), 'image');
  const b = withMediaKind(withFilters(library(), (i) => {
    i.filters.photo.includePeople = ['p1', 'p2'];
  }), 'image');
  assert.equal(queryFingerprint(a), queryFingerprint(b));
});

test('the album id is part of the identity', () => {
  assert.notEqual(
    queryFingerprint(emptyIdentity({ kind: 'album', albumId: 'a-1' })),
    queryFingerprint(emptyIdentity({ kind: 'album', albumId: 'a-2' })),
  );
  assert.notEqual(queryFingerprint(library()), queryFingerprint(album()));
});

// -------------------------------------------------------------------- chips

test('chips describe only filters applied to the visible results', () => {
  const identity = withMediaKind(
    withFilters(library(), (i) => {
      i.filters.common.metadataQuery = 'mare';
      i.filters.common.favorite = true;
      i.filters.video.codec = 'h264';
    }),
    'image',
  );
  const kinds = buildFilterChips(identity).map((c) => c.kind);
  assert.deepEqual(kinds, ['metadata', 'favorite']);
  assert.ok(!kinds.includes('codec'), 'a retained video filter is not applied here');
});

test('clearing on one tab preserves the other kinds retained filters', () => {
  const identity = withMediaKind(
    withFilters(library(), (i) => {
      i.filters.common.favorite = true;
      i.filters.photo.includePeople = ['p1'];
      i.filters.video.codec = 'h264';
    }),
    'video',
  );
  const cleared = clearActiveFilters(identity);
  assert.equal(cleared.common.favorite, null);
  assert.equal(cleared.video.codec, '');
  assert.deepEqual(cleared.photo.includePeople, ['p1'],
    'the photo selection must survive a clear performed on the Videos tab');
});

// -------------------------------------------------------------------- units

test('duration presets convert to the canonical wire seconds', () => {
  assert.equal(minutesToSeconds(5), 300);
  assert.equal(minutesToSeconds(null), null);
  assert.equal(secondsToMinutes(900), 15);
  assert.equal(secondsToMinutes(null), null);
});

test('a cursor and a non-default limit reach the wire', () => {
  const wire = queryToWire(library(), 'CURSOR', 24);
  assert.equal(wire.cursor, 'CURSOR');
  assert.equal(wire.limit, '24');
  // An empty cursor is "first page", not a cursor.
  assert.equal(queryToWire(library(), '').cursor, undefined);
});

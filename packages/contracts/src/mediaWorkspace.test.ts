// The workspace filter model (§45, §46).
//
// Every case here is asserted against the WIRE, because that is the only thing
// two clients can actually disagree about. The model is shared, so proving it
// once proves it for the phone and the browser at the same time — which is the
// entire reason it was moved out of the web app.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  buildFilterChips,
  clearActiveFilters,
  clearChip,
  emptyIdentity,
  hasActiveFilters,
  queryFingerprint,
  queryToWire,
  type MediaWorkspaceIdentity,
} from './mediaWorkspace.ts';
import { isSemanticActive } from './mediaWorkspace.ts';
import { mediaQueryToParams } from './media.ts';
import { toQueryString } from './query.ts';

const LIBRARY = { kind: 'library' as const };
const ALBUM = { kind: 'album' as const, albumId: 'alb-1' };

function identity(over: (i: MediaWorkspaceIdentity) => void = () => {}): MediaWorkspaceIdentity {
  const i = emptyIdentity(LIBRARY);
  over(i);
  return i;
}
/** The exact query string this identity would send. */
const wire = (i: MediaWorkspaceIdentity, cursor: string | null = null) =>
  toQueryString(mediaQueryToParams(queryToWire(i, cursor)));

// ── defaults ────────────────────────────────────────────────────────────────

test('the default state sends no filter at all', () => {
  assert.equal(wire(identity()), 'kind=all&sort=created&direction=desc&limit=50');
  assert.equal(hasActiveFilters(identity()), false);
  assert.deepEqual(buildFilterChips(identity()), []);
});

// ── common filters (§9) ─────────────────────────────────────────────────────

test('each common filter reaches the wire on its own', () => {
  const cases: Array<[(i: MediaWorkspaceIdentity) => void, string]> = [
    [(i) => { i.filters.common.metadataQuery = 'mare'; }, 'q=mare'],
    [(i) => { i.filters.common.favorite = true; }, 'favorite=true'],
    [(i) => { i.filters.common.favorite = false; }, 'favorite=false'],
    [(i) => { i.filters.common.minRating = 4; }, 'minRating=4'],
    [(i) => { i.filters.common.dateTakenFrom = '2026-01-01T00:00:00.000Z'; },
      'dateTakenFrom=2026-01-01T00%3A00%3A00.000Z'],
    [(i) => { i.filters.common.albumMembership = 'unassigned'; }, 'albumMembership=unassigned'],
    [(i) => { i.libraryScope = 'excluded'; }, 'scope=excluded'],
    [(i) => { i.sort = 'datetaken'; i.direction = 'asc'; }, 'sort=datetaken&direction=asc'],
  ];
  for (const [mutate, expected] of cases) {
    assert.ok(wire(identity(mutate)).includes(expected), expected);
  }
});

test('combining filters keeps every one of them', () => {
  const q = wire(identity((i) => {
    i.filters.common.favorite = true;
    i.filters.common.minRating = 4;
    i.filters.common.metadataQuery = 'estate';
  }));
  for (const part of ['q=estate', 'favorite=true', 'minRating=4']) {
    assert.ok(q.includes(part), part);
  }
});

test('album membership is a LIBRARY concern and is dropped inside an album', () => {
  // Every item in an album is a member, so the filter is meaningless there and
  // the backend rejects it.
  const inAlbum = emptyIdentity(ALBUM);
  inAlbum.filters.common.albumMembership = 'unassigned';
  assert.ok(!wire(inAlbum).includes('albumMembership'));
  assert.deepEqual(buildFilterChips(inAlbum), []);
});

// ── kind compatibility: the rule that keeps a 400 off the wire (§46) ────────

test('photo filters are NEVER sent on the all or video tabs', () => {
  const withPhoto = (kind: 'all' | 'image' | 'video') => identity((i) => {
    i.mediaKind = kind;
    i.filters.photo.hasGps = true;
    i.filters.photo.collapseDuplicates = true;
    i.filters.photo.includePeople = ['p1'];
  });
  assert.ok(wire(withPhoto('image')).includes('hasGps=true'));
  for (const kind of ['all', 'video'] as const) {
    const q = wire(withPhoto(kind));
    for (const forbidden of ['hasGps', 'collapseDuplicates', 'includePeople']) {
      assert.ok(!q.includes(forbidden), `${forbidden} leaked onto kind=${kind}`);
    }
  }
});

test('video filters are NEVER sent on the all or image tabs', () => {
  const withVideo = (kind: 'all' | 'image' | 'video') => identity((i) => {
    i.mediaKind = kind;
    i.filters.video.durationMinSeconds = 10;
    i.filters.video.hasAudio = true;
    i.filters.video.codec = 'h264';
  });
  assert.ok(wire(withVideo('video')).includes('durationMin=10'));
  for (const kind of ['all', 'image'] as const) {
    const q = wire(withVideo(kind));
    for (const forbidden of ['durationMin', 'hasAudio', 'codec']) {
      assert.ok(!q.includes(forbidden), `${forbidden} leaked onto kind=${kind}`);
    }
  }
});

test('a chip never claims a filter the results are not actually under', () => {
  const i = identity((x) => {
    x.mediaKind = 'video';
    x.filters.photo.hasGps = true;      // retained, but inert here
    x.filters.video.hasAudio = true;
  });
  const kinds = buildFilterChips(i).map((c) => c.kind);
  assert.ok(kinds.includes('has-audio'));
  assert.ok(!kinds.includes('gps'));
});

// ── video filters (§17) ─────────────────────────────────────────────────────

test('video filters serialize with the canonical units', () => {
  const q = wire(identity((i) => {
    i.mediaKind = 'video';
    i.filters.video.durationMinSeconds = 10;
    i.filters.video.durationMaxSeconds = 600;
    i.filters.video.minHeight = 1080;
    i.filters.video.codec = 'h264';
    i.filters.video.hasAudio = false;
  }));
  for (const part of ['durationMin=10', 'durationMax=600', 'minHeight=1080',
    'codec=h264', 'hasAudio=false']) {
    assert.ok(q.includes(part), part);
  }
});

// ── People (§45) ────────────────────────────────────────────────────────────

const photos = (mutate: (i: MediaWorkspaceIdentity) => void) => identity((i) => {
  i.mediaKind = 'image';
  mutate(i);
});

test('one included person', () => {
  assert.ok(wire(photos((i) => { i.filters.photo.includePeople = ['p1']; }))
    .includes('includePeople=p1&includePeopleMode=all'));
});

test('multiple included people with all, and with any', () => {
  const q = (mode: 'all' | 'any') => wire(photos((i) => {
    i.filters.photo.includePeople = ['p1', 'p2'];
    i.filters.photo.includePeopleMode = mode;
  }));
  assert.ok(q('all').includes('includePeople=p1%2Cp2&includePeopleMode=all'));
  assert.ok(q('any').includes('includePeople=p1%2Cp2&includePeopleMode=any'));
});

test('one excluded person, and include combined with exclude', () => {
  assert.ok(wire(photos((i) => { i.filters.photo.excludePeople = ['p3']; }))
    .includes('excludePeople=p3'));
  const both = wire(photos((i) => {
    i.filters.photo.includePeople = ['p1'];
    i.filters.photo.excludePeople = ['p3'];
  }));
  assert.ok(both.includes('includePeople=p1'));
  assert.ok(both.includes('excludePeople=p3'));
});

test('the mode is not sent when nobody is included', () => {
  // "Match all of nobody" is not a filter; sending it would key the cursor on
  // a dimension the user never chose.
  assert.ok(!wire(photos((i) => {
    i.filters.photo.excludePeople = ['p3'];
    i.filters.photo.includePeopleMode = 'any';
  })).includes('includePeopleMode'));
});

test('clearing ONE person chip leaves the other People filter standing', () => {
  const base = photos((i) => {
    i.filters.photo.includePeople = ['p1', 'p2'];
    i.filters.photo.excludePeople = ['p3'];
  });
  const afterInclude = clearChip(base.filters, 'people-include');
  assert.deepEqual(afterInclude.photo.includePeople, []);
  assert.deepEqual(afterInclude.photo.excludePeople, ['p3']);

  const afterExclude = clearChip(base.filters, 'people-exclude');
  assert.deepEqual(afterExclude.photo.includePeople, ['p1', 'p2']);
  assert.deepEqual(afterExclude.photo.excludePeople, []);
});

test('clearing a person does not disturb the other filters', () => {
  const base = photos((i) => {
    i.filters.common.favorite = true;
    i.filters.common.minRating = 3;
    i.filters.photo.includePeople = ['p1'];
  });
  const next = clearChip(base.filters, 'people-include');
  assert.equal(next.common.favorite, true);
  assert.equal(next.common.minRating, 3);
});

test('People combine with every compatible filter at once', () => {
  const q = wire(photos((i) => {
    i.filters.common.favorite = true;
    i.filters.common.minRating = 4;
    i.filters.common.dateTakenFrom = '2026-01-01T00:00:00.000Z';
    i.filters.common.albumMembership = 'assigned';
    i.filters.photo.hasGps = true;
    i.filters.photo.includePeople = ['p1', 'p2'];
    i.filters.photo.excludePeople = ['p9'];
    i.filters.photo.includePeopleMode = 'any';
  }));
  for (const part of ['kind=image', 'favorite=true', 'minRating=4', 'dateTakenFrom=',
    'albumMembership=assigned', 'hasGps=true', 'includePeople=p1%2Cp2',
    'excludePeople=p9', 'includePeopleMode=any']) {
    assert.ok(q.includes(part), part);
  }
});

test('a person filter is identity, so it survives a display-name change', () => {
  // Nothing in the query or the fingerprint mentions a name: renaming somebody
  // cannot retarget or invalidate a filter.
  const i = photos((x) => { x.filters.photo.includePeople = ['p1']; });
  assert.ok(!wire(i).includes('Mario'));
  assert.ok(!queryFingerprint(i).includes('Mario'));
  const chip = buildFilterChips(i).find((c) => c.kind === 'people-include');
  assert.deepEqual(chip?.personIds, ['p1']);
  assert.equal(chip?.text, undefined); // ids only; the UI resolves the label
});

// ── clearing (§46) ──────────────────────────────────────────────────────────

test('clear all wipes the visible filters and keeps the other tab retained', () => {
  const i = identity((x) => {
    x.mediaKind = 'video';
    x.filters.common.favorite = true;
    x.filters.video.hasAudio = true;
    x.filters.photo.includePeople = ['p1']; // retained for the photo tab
  });
  const next = clearActiveFilters(i);
  assert.equal(next.common.favorite, null);
  assert.equal(next.video.hasAudio, null);
  assert.deepEqual(next.photo.includePeople, ['p1']);
});

// ── query generation (§19) ──────────────────────────────────────────────────

test('any semantic change starts a NEW query generation', () => {
  const base = identity();
  const changes: Array<(i: MediaWorkspaceIdentity) => void> = [
    (i) => { i.filters.common.favorite = true; },
    (i) => { i.filters.common.minRating = 1; },
    (i) => { i.mediaKind = 'image'; },
    (i) => { i.libraryScope = 'excluded'; },
    (i) => { i.sort = 'name'; },
    (i) => { i.direction = 'asc'; },
  ];
  for (const mutate of changes) {
    assert.notEqual(queryFingerprint(identity(mutate)), queryFingerprint(base));
  }
});

test('the cursor is NOT part of the query identity', () => {
  // Paging must not look like a new query, or every page would reset the list.
  const i = identity();
  assert.equal(queryFingerprint(i), queryFingerprint(i));
  assert.notEqual(wire(i, 'cur-2'), wire(i, null));
  assert.ok(wire(i, 'cur-2').includes('cursor=cur-2'));
});

test('People order does not change the query identity', () => {
  // Selecting the same two people in the other order is the same question.
  const a = photos((i) => { i.filters.photo.includePeople = ['p1', 'p2']; });
  const b = photos((i) => { i.filters.photo.includePeople = ['p2', 'p1']; });
  assert.equal(queryFingerprint(a), queryFingerprint(b));
});

test('a retained filter for another kind does not change this kind identity', () => {
  const bare = identity((i) => { i.mediaKind = 'video'; });
  const withRetainedPhoto = identity((i) => {
    i.mediaKind = 'video';
    i.filters.photo.hasGps = true;
  });
  assert.equal(queryFingerprint(bare), queryFingerprint(withRetainedPhoto));
});

// ── semantic search (§10) ───────────────────────────────────────────────────

const inAlbum = (kind: 'all' | 'image' | 'video') => {
  const i = emptyIdentity(ALBUM);
  i.mediaKind = kind;
  return i;
};

test('visual search works on VIDEOS, not only photos', () => {
  // /api/media/semantic takes kind = all | image | video. Treating visual
  // search as a photo-only feature would remove a capability the server has.
  for (const kind of ['all', 'image', 'video'] as const) {
    const active = identity((i) => {
      i.mediaKind = kind;
      i.filters.photo.visualQuery = 'mare';
    });
    assert.equal(isSemanticActive(active), true, `library/${kind}`);
  }
});

test('visual search is CONFINED to an album, not hidden there', () => {
  // The semantic route takes an optional albumId, so an album search is
  // answered from that album — photos and videos alike. The alternatives it
  // replaces were both bad: hide the control, or answer from the whole library
  // and let the results read as if they were the album's.
  for (const kind of ['all', 'image', 'video'] as const) {
    const inside = inAlbum(kind);
    inside.filters.photo.visualQuery = 'mare';
    assert.equal(isSemanticActive(inside), true, `album/${kind}`);
  }
});

test('an empty query is not a search, anywhere', () => {
  assert.equal(isSemanticActive(identity()), false);
  assert.equal(isSemanticActive(inAlbum('image')), false);
  assert.equal(isSemanticActive(identity((i) => { i.filters.photo.visualQuery = 'mare'; })), true);
});

test('whitespace is not a visual query', () => {
  assert.equal(isSemanticActive(identity((i) => { i.filters.photo.visualQuery = '   '; })), false);
});

test('a visual query never rides the PHYSICAL listing', () => {
  // The unified endpoint carries no semantic term: relevance ranking has its
  // own cursor and its own route. Emitting it here would be a filter the
  // server silently ignores.
  const q = wire(identity((i) => { i.filters.photo.visualQuery = 'mare'; }));
  assert.ok(!q.includes('mare'));
  assert.ok(!q.includes('semantic'));
});

test('a visual query shows its chip everywhere it is running', () => {
  const kinds = (i: MediaWorkspaceIdentity) => buildFilterChips(i).map((c) => c.kind);
  assert.ok(kinds(identity((i) => { i.filters.photo.visualQuery = 'mare'; })).includes('visual'));

  for (const kind of ['all', 'image', 'video'] as const) {
    const inside = inAlbum(kind);
    inside.filters.photo.visualQuery = 'mare';
    assert.ok(kinds(inside).includes('visual'), `album/${kind}`);
  }
});

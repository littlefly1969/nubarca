import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  isTvFilterActive,
  resolveTvFilterFocus,
  TV_FILTER_DESCRIPTORS,
  TV_FILTER_OWNER,
  tvFilterApplies,
  tvFilterRows,
  type TvFilterId,
} from './tvFilterCatalog.ts';
import {
  activeFilterCount,
  buildFilterChips,
  cloneMediaFilters,
  emptyIdentity,
  queryToWire,
  semanticToWire,
  type MediaKindScope,
  type MediaWorkspaceFilters,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from './mediaWorkspaceQuery.ts';

// What this file protects is the INVARIANT the television lost: every filter
// the panel shows can actually be operated from a remote and actually reaches
// the server, and every filter it hides cannot reach the server at all.
//
// The panel's own half of that — that each row id has a control that edits it —
// is enforced by the compiler rather than here: LibraryFilterPanel builds its
// views as a `Record<TvFilterId, RowView>`, so a row the catalog can produce and
// the panel cannot draw does not build, and `TvFilterEditor` has no read-only
// member for a row to fall back to. What a test can add is the other half: that
// each row is bindable to a real value, that the value travels, and that a
// hidden row's value never does.

const LIBRARY: MediaWorkspaceSource = { kind: 'library' };
const ALBUM: MediaWorkspaceSource = { kind: 'album', albumId: 'a-1' };
const SOURCES: readonly MediaWorkspaceSource[] = [LIBRARY, ALBUM];
const KINDS: readonly MediaKindScope[] = ['all', 'image', 'video'];

function identityFor(source: MediaWorkspaceSource, kind: MediaKindScope): MediaWorkspaceIdentity {
  return { ...emptyIdentity(source), mediaKind: kind };
}

// A representative non-default value for every row, used to prove each one is
// bindable and reaches the wire. The Record type makes it complete: a new
// TvFilterId has to be given a value here before this file compiles.
const SET_VALUE: Record<TvFilterId, (f: MediaWorkspaceFilters) => void> = {
  metadataQuery: (f) => { f.common.metadataQuery = 'vacanze'; },
  semanticQuery: (f) => { f.common.visualQuery = 'un cane sulla spiaggia'; f.common.semanticTopK = 200; },
  favorite: (f) => { f.common.favorite = true; },
  minRating: (f) => { f.common.minRating = 4; },
  period: (f) => {
    f.common.dateTakenFrom = '2026-05-01T00:00:00.000Z';
    f.common.dateTakenTo = '2026-06-01T00:00:00.000Z';
  },
  albumMembership: (f) => { f.common.albumMembership = 'unassigned'; },
  people: (f) => {
    f.photo.includePeople = ['p-1', 'p-2'];
    f.photo.excludePeople = ['p-3'];
    f.photo.includePeopleMode = 'any';
  },
  hasGps: (f) => { f.photo.hasGps = true; },
  collapseDuplicates: (f) => { f.photo.collapseDuplicates = true; },
  durationMin: (f) => { f.video.durationMinSeconds = 60; },
  durationMax: (f) => { f.video.durationMaxSeconds = 1800; },
  minHeight: (f) => { f.video.minHeight = 1080; },
  codec: (f) => { f.video.codec = 'hevc'; },
  hasAudio: (f) => { f.video.hasAudio = false; },
};

function filtersWith(id: TvFilterId): MediaWorkspaceFilters {
  const filters = cloneMediaFilters(emptyIdentity(LIBRARY).filters);
  SET_VALUE[id](filters);
  return filters;
}

const ALL_IDS = Object.keys(SET_VALUE) as TvFilterId[];

// ------------------------------------------------------- the catalog itself

test('every domain filter field is claimed by exactly one TV row', () => {
  // The `satisfies Record<DomainFilterField, TvFilterId>` in the catalog is what
  // makes a NEW field a build failure; this is the other direction — every id a
  // field names must be a row that exists and can be drawn.
  const described = new Set(TV_FILTER_DESCRIPTORS.map((d) => d.id));
  for (const owner of Object.values(TV_FILTER_OWNER)) {
    assert.ok(described.has(owner), `field owner ${owner} has no descriptor`);
  }
  // And no descriptor is an orphan that no domain field feeds.
  const owners = new Set<string>(Object.values(TV_FILTER_OWNER));
  for (const descriptor of TV_FILTER_DESCRIPTORS) {
    assert.ok(owners.has(descriptor.id), `row ${descriptor.id} owns no domain field`);
  }
  assert.equal(described.size, TV_FILTER_DESCRIPTORS.length, 'duplicate descriptor id');
});

test('every row has an operable editor — there is no summary-only row', () => {
  const operable = new Set(['cycle', 'text', 'period', 'people']);
  for (const descriptor of TV_FILTER_DESCRIPTORS) {
    assert.ok(operable.has(descriptor.editor), `${descriptor.id} has no usable editor`);
    assert.ok(descriptor.wireKeys.length > 0, `${descriptor.id} can never reach the server`);
  }
});

test('every row can actually become active — none is decorative', () => {
  const empty = emptyIdentity(LIBRARY).filters;
  for (const id of ALL_IDS) {
    assert.equal(isTvFilterActive(id, empty), false, `${id} starts active`);
    assert.equal(isTvFilterActive(id, filtersWith(id)), true, `${id} cannot be set`);
  }
});

// --------------------------------------------------------- what is shown

test('applicability follows the tab and the source', () => {
  const shown = (source: MediaWorkspaceSource, kind: MediaKindScope) =>
    tvFilterRows(identityFor(source, kind), emptyIdentity(source).filters).map((r) => r.id);

  assert.deepEqual(shown(LIBRARY, 'all'),
    ['metadataQuery', 'favorite', 'minRating', 'period', 'albumMembership']);
  assert.deepEqual(shown(LIBRARY, 'image'), [
    'metadataQuery', 'favorite', 'minRating', 'period', 'albumMembership',
    'people', 'hasGps', 'collapseDuplicates',
  ]);
  assert.deepEqual(shown(LIBRARY, 'video'), [
    'metadataQuery', 'favorite', 'minRating', 'period', 'albumMembership',
    'durationMin', 'durationMax', 'minHeight', 'codec', 'hasAudio',
  ]);
  // Inside an album every item is a member, so the row is absent rather than
  // shown and ignored — and the album endpoint does not accept the parameter.
  assert.ok(!shown(ALBUM, 'all').includes('albumMembership'));
  assert.ok(!shown(ALBUM, 'image').includes('albumMembership'));
  // Semantic retrieval for mixed media is library-scoped, so inside an album it
  // is offered on the Photos tab (album-scoped there) and nowhere else — rather
  // than offered everywhere and silently searching the whole library.
  // Modelled and tested, deliberately NOT OFFERED until the TV-personal
  // semantic adapter exists: a filter the user can set that changes nothing
  // they can see is the exact defect this module prevents.
  for (const source of SOURCES) {
    for (const kind of KINDS) {
      assert.ok(!shown(source, kind).includes('semanticQuery'),
        `semanticQuery must not be offered on ${source.kind}/${kind} without its backend`);
    }
  }
});

test('the people row is shown on Photos and is an editor, not a readout', () => {
  const identity = identityFor(LIBRARY, 'image');
  const row = tvFilterRows(identity, identity.filters).find((r) => r.id === 'people');
  assert.ok(row, 'the Photos tab must offer a people row');
  assert.equal(row.editor, 'people');
  assert.equal(row.active, false);
  // The regression itself: from an EMPTY selection the row must still be an
  // editor. It used to be a summary whose only action was to clear, so SELECT
  // on this exact state wrote [] over [] and the filter was unreachable.
  assert.notEqual(row.editor, 'cycle');
});

// ------------------------------------------- panel and wire are the same rule

test('an applicable row reaches the wire; a hidden one never does', () => {
  for (const descriptor of TV_FILTER_DESCRIPTORS) {
    const id = descriptor.id;
    for (const source of SOURCES) {
      for (const kind of KINDS) {
        const identity: MediaWorkspaceIdentity = {
          ...identityFor(source, kind), filters: filtersWith(id),
        };
        // Each transport is checked against ITS OWN builder. Semantic
        // retrieval is a different endpoint with its own relevance cursor, so
        // queryToWire deliberately never emits it — asserting otherwise would
        // force the semantic query to be smuggled onto the structural list.
        const wire = descriptor.transport === 'semantic'
          ? semanticToWire(identity, null)
          : queryToWire(identity, null);
        const emitted = descriptor.wireKeys.filter((key) => key in wire);
        const rows = tvFilterRows(identity, identity.filters);
        const visible = rows.some((row) => row.id === id);

        assert.equal(visible, tvFilterApplies(descriptor, identity));
        if (visible) {
          assert.ok(emitted.length > 0,
            `${id} is shown on ${source.kind}/${kind} but cannot be sent`);
        } else {
          assert.deepEqual(emitted, [],
            `${id} is hidden on ${source.kind}/${kind} but reached the wire as ${emitted.join()}`);
        }
      }
    }
  }
});

test('row activity and the applied-filter chips never disagree', () => {
  // The grid's badge counts CHIPS and the panel marks ROWS; they are two
  // projections of one state and must not be able to tell different stories.
  const CHIP_ROW: Record<string, TvFilterId> = {
    metadata: 'metadataQuery',
    semantic: 'semanticQuery',
    favorite: 'favorite',
    'min-rating': 'minRating',
    date: 'period',
    'album-membership': 'albumMembership',
    'people-include': 'people',
    'people-exclude': 'people',
    gps: 'hasGps',
    collapse: 'collapseDuplicates',
    duration: 'durationMin',
    'min-height': 'minHeight',
    codec: 'codec',
    'has-audio': 'hasAudio',
  };
  for (const id of ALL_IDS) {
    for (const source of SOURCES) {
      for (const kind of KINDS) {
        const identity: MediaWorkspaceIdentity = {
          ...identityFor(source, kind), filters: filtersWith(id),
        };
        const rows = tvFilterRows(identity, identity.filters);
        const chipRows = new Set(buildFilterChips(identity).map((c) => CHIP_ROW[c.kind]));
        for (const row of rows) {
          if (row.active) {
            // durationMax shares the single 'duration' chip with durationMin.
            const covered = chipRows.has(row.id)
              || (row.id === 'durationMax' && chipRows.has('durationMin'));
            assert.ok(covered, `${row.id} is marked active with no chip behind it`);
          }
        }
        for (const chipRow of chipRows) {
          assert.ok(rows.some((row) => row.id === chipRow && row.active)
            || (chipRow === 'durationMin' && rows.some((r) => r.id === 'durationMax' && r.active)),
          `a ${chipRow} chip is applied with no active row for it`);
        }
      }
    }
  }
});

// ------------------------------------------------------------------- focus

test('focus starts on the first row of the current tab', () => {
  const identity = identityFor(LIBRARY, 'image');
  const rows = tvFilterRows(identity, identity.filters);
  assert.equal(resolveTvFilterFocus(null, rows), 'metadataQuery');
});

test('a row that is still there keeps focus — a sub-editor returns to its opener', () => {
  const identity = identityFor(LIBRARY, 'image');
  const before = tvFilterRows(identity, identity.filters);
  // Opening the picker, choosing someone and coming back changes the DRAFT,
  // never the row set: the remote lands back on the exact row it left.
  const after = tvFilterRows(identity, filtersWith('people'));
  assert.equal(resolveTvFilterFocus('people', before), 'people');
  assert.equal(resolveTvFilterFocus('people', after), 'people');
  assert.equal(after.find((r) => r.id === 'people')?.active, true);
});

test('a removed row hands focus to the nearest surviving row', () => {
  const photo = identityFor(LIBRARY, 'image');
  const video = identityFor(LIBRARY, 'video');
  const videoRows = tvFilterRows(video, video.filters);
  // Photos → Videos with the remote on "Persone": that row is gone, so focus
  // migrates deterministically instead of being left on the container. People
  // is the first photo row, so the nearest survivor is the common row directly
  // above where it sat — which is also where it sat on screen.
  assert.ok(tvFilterRows(photo, photo.filters).some((r) => r.id === 'people'));
  assert.ok(!videoRows.some((r) => r.id === 'people'));
  assert.equal(resolveTvFilterFocus('people', videoRows), 'albumMembership');
  // hasGps is equidistant between the last common row and the first video row.
  // The tie goes to the EARLIER row, so the answer is a property of the catalog
  // order rather than of iteration luck.
  assert.equal(resolveTvFilterFocus('hasGps', videoRows), 'albumMembership');
  // Further down the photo section, the video rows win outright.
  assert.equal(resolveTvFilterFocus('collapseDuplicates', videoRows), 'durationMin');

  // Library → album drops the membership row; the neighbour above it wins the
  // tie, so the choice is stable rather than order-of-iteration luck.
  const albumRows = tvFilterRows(identityFor(ALBUM, 'all'), emptyIdentity(ALBUM).filters);
  assert.equal(resolveTvFilterFocus('albumMembership', albumRows), 'period');
});

test('resolving focus is idempotent — re-feeding the answer changes nothing', () => {
  // The panel resolves focus during render and writes the answer back to the
  // ref it just read, so the resolver sees its own output on the next render
  // (and on a discarded one, under StrictMode or a concurrent re-render). That
  // is only safe because a resolved key is always a fixed point: it is either a
  // static control or a row that is present. Without this, a render that did
  // not commit could walk the focus target away from where the remote is.
  const photo = identityFor(LIBRARY, 'image');
  const photoRows = tvFilterRows(photo, photo.filters);
  const videoRows = tvFilterRows(identityFor(LIBRARY, 'video'), emptyIdentity(LIBRARY).filters);
  const albumRows = tvFilterRows(identityFor(ALBUM, 'all'), emptyIdentity(ALBUM).filters);

  const starts: (TvFilterId | 'sort' | 'apply' | null)[] = [
    null, 'people', 'hasGps', 'albumMembership', 'codec', 'sort', 'apply',
  ];
  for (const rows of [photoRows, videoRows, albumRows, []]) {
    for (const start of starts) {
      const once = resolveTvFilterFocus(start, rows);
      assert.equal(resolveTvFilterFocus(once, rows), once,
        `resolving ${String(start)} twice moved the target`);
    }
  }
});

test('focus never resolves to nothing', () => {
  // The static controls exist on every tab, so they always survive…
  for (const key of ['sort', 'direction', 'reset', 'apply', 'cancel'] as const) {
    assert.equal(resolveTvFilterFocus(key, []), key);
  }
  // …and with no rows at all the primary action is the terminus. A resolver
  // that could answer null is what leaves native focus on the bare container
  // with no visible ring and no working direction key.
  assert.equal(resolveTvFilterFocus(null, []), 'apply');
  assert.equal(resolveTvFilterFocus('people', []), 'apply');
});

// ------------------------------------------------------------- integration

// The named regression: a person filter that did not exist, chosen with the
// remote, arriving at the server — the whole path the television could not walk.
test('people: from an empty Photos gallery to the wire request', () => {
  const committed: MediaWorkspaceIdentity = identityFor(LIBRARY, 'image');
  assert.equal(activeFilterCount(committed), 0);

  // The panel opens on a draft that shares nothing with the committed query.
  const draft = cloneMediaFilters(committed.filters);

  // The people row is reachable and opens the picker.
  const opener = tvFilterRows(committed, draft).find((r) => r.id === 'people');
  assert.equal(opener?.editor, 'people');

  // The picker selects one person, then a second, then sets ANY.
  draft.photo.includePeople = ['person-a'];
  draft.photo.includePeople = [...draft.photo.includePeople, 'person-b'];
  draft.photo.includePeopleMode = 'any';

  // BACK to the panel keeps the edit and marks the row active…
  assert.equal(tvFilterRows(committed, draft).find((r) => r.id === 'people')?.active, true);
  // …while the committed query — and therefore the grid — has not moved.
  assert.equal(activeFilterCount(committed), 0);
  assert.deepEqual(committed.filters.photo.includePeople, []);

  // Apply commits the draft and the request carries the ids and the mode.
  const applied: MediaWorkspaceIdentity = { ...committed, filters: draft };
  const wire = queryToWire(applied, null);
  assert.equal(wire.includePeople, 'person-a,person-b');
  assert.equal(wire.includePeopleMode, 'any');
  assert.equal(wire.kind, 'image');
  assert.equal(activeFilterCount(applied), 1);
});

test('people: exclusions and ALL mode travel too', () => {
  const identity: MediaWorkspaceIdentity = {
    ...identityFor(LIBRARY, 'image'), filters: filtersWith('people'),
  };
  const anyWire = queryToWire(identity, null);
  assert.equal(anyWire.includePeople, 'p-1,p-2');
  assert.equal(anyWire.excludePeople, 'p-3');
  assert.equal(anyWire.includePeopleMode, 'any');

  const allFilters = cloneMediaFilters(identity.filters);
  allFilters.photo.includePeopleMode = 'all';
  assert.equal(queryToWire({ ...identity, filters: allFilters }, null).includePeopleMode, 'all');
});

test('a boolean, an enum and a range each travel from their row to the wire', () => {
  const boolean: MediaWorkspaceIdentity = {
    ...identityFor(LIBRARY, 'image'), filters: filtersWith('collapseDuplicates'),
  };
  assert.equal(queryToWire(boolean, null).collapseDuplicates, 'true');

  const enumRow: MediaWorkspaceIdentity = {
    ...identityFor(LIBRARY, 'all'), filters: filtersWith('albumMembership'),
  };
  assert.equal(queryToWire(enumRow, null).albumMembership, 'unassigned');

  const range: MediaWorkspaceIdentity = {
    ...identityFor(LIBRARY, 'all'), filters: filtersWith('period'),
  };
  const wire = queryToWire(range, null);
  assert.equal(wire.dateTakenFrom, '2026-05-01T00:00:00.000Z');
  // The upper bound had no editor on the television at all, so it could only
  // ever be empty here.
  assert.equal(wire.dateTakenTo, '2026-06-01T00:00:00.000Z');

  const durations: MediaWorkspaceIdentity = {
    ...identityFor(LIBRARY, 'video'), filters: filtersWith('durationMin'),
  };
  assert.equal(queryToWire(durations, null).durationMin, '60');
});

test('switching Photos → Videos strands no filter on the wire', () => {
  const photos: MediaWorkspaceIdentity = {
    ...identityFor(LIBRARY, 'image'), filters: filtersWith('people'),
  };
  assert.ok('includePeople' in queryToWire(photos, null));

  // The selection is RETAINED (Photos → Videos → Photos keeps it) but the
  // Videos tab neither shows it nor sends it.
  const videos: MediaWorkspaceIdentity = { ...photos, mediaKind: 'video' };
  const wire = queryToWire(videos, null);
  for (const key of ['includePeople', 'excludePeople', 'includePeopleMode', 'hasGps', 'collapseDuplicates']) {
    assert.ok(!(key in wire), `${key} survived the switch to Videos`);
  }
  assert.deepEqual(videos.filters.photo.includePeople, ['p-1', 'p-2']);
  assert.ok(!tvFilterRows(videos, videos.filters).some((r) => r.id === 'people'));
});

test('a cancelled panel cannot have touched the committed query', () => {
  const committed = identityFor(LIBRARY, 'image');
  const draft = cloneMediaFilters(committed.filters);
  // Everything an editor could do to the draft, including the array mutations
  // a shallow clone would have shared with the committed filters.
  for (const id of ALL_IDS) SET_VALUE[id](draft);
  draft.photo.includePeople.push('person-z');

  assert.equal(activeFilterCount(committed), 0);
  assert.deepEqual(committed.filters, emptyIdentity(LIBRARY).filters);
  assert.deepEqual(queryToWire(committed, null), {
    kind: 'image', sort: 'created', direction: 'desc', limit: '50',
  });
});

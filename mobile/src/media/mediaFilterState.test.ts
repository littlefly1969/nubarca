// Mobile filter-state tests (§45, §46).
//
// The domain rules are proven once in the shared contract; what is proven HERE
// is the phone's own interaction shape: the draft/apply split, chip removal,
// People selection, and the query generation that drives cursor reset.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mediaQueryToParams, toQueryString } from '@nubarca/contracts';
import {
  chipsFor,
  draftFrom,
  generationOf,
  initialIdentity,
  pageQuery,
  personSide,
  referencedPersonIds,
  togglePerson,
  withChipCleared,
  withFilters,
  withFiltersCleared,
  withPeopleMode,
} from './mediaFilterState.ts';

const wire = (i: ReturnType<typeof initialIdentity>, cursor: string | null = null) =>
  toQueryString(mediaQueryToParams(pageQuery(i, cursor, 60)));

test('a photo tab starts unfiltered, newest capture first', () => {
  const i = initialIdentity('image');
  assert.equal(wire(i), 'kind=image&sort=datetaken&direction=desc&limit=60');
  assert.deepEqual(chipsFor(i), []);
});

// ── draft / apply (§7) ──────────────────────────────────────────────────────

test('editing a draft does not touch the applied query', () => {
  const applied = initialIdentity('image');
  const draft = draftFrom(applied);
  draft.filters.common.favorite = true;
  draft.filters.photo.includePeople.push('p1');

  assert.equal(applied.filters.common.favorite, null);
  assert.deepEqual(applied.filters.photo.includePeople, []);
  // Nothing refetches while the sheet is open: the generation is unchanged.
  assert.equal(generationOf(applied), generationOf(initialIdentity('image')));
});

test('applying the draft commits every edit at once', () => {
  const applied = initialIdentity('image');
  const draft = draftFrom(applied);
  draft.filters.common.favorite = true;
  draft.filters.common.minRating = 4;
  const next = withFilters(applied, draft.filters);
  const q = wire(next);
  assert.ok(q.includes('favorite=true'));
  assert.ok(q.includes('minRating=4'));
  assert.notEqual(generationOf(next), generationOf(applied));
});

test('a discarded draft leaves nothing behind', () => {
  const applied = initialIdentity('image');
  const before = generationOf(applied);
  const draft = draftFrom(applied);
  draft.filters.photo.excludePeople.push('p9');
  draft.filters.video.codec = 'h264';
  assert.equal(generationOf(applied), before);
});

// ── chips (§18) ─────────────────────────────────────────────────────────────

test('removing one chip clears only its own field', () => {
  let i = initialIdentity('image');
  i = withFilters(i, {
    ...i.filters,
    common: { ...i.filters.common, favorite: true, minRating: 3 },
    photo: { ...i.filters.photo, hasGps: true },
  });
  assert.equal(chipsFor(i).length, 3);

  const afterFavorite = withChipCleared(i, 'favorite');
  assert.equal(afterFavorite.filters.common.favorite, null);
  assert.equal(afterFavorite.filters.common.minRating, 3);
  assert.equal(afterFavorite.filters.photo.hasGps, true);
});

test('clear all empties the visible chips', () => {
  let i = initialIdentity('image');
  i = withFilters(i, {
    ...i.filters,
    common: { ...i.filters.common, favorite: true },
    photo: { ...i.filters.photo, hasGps: true, includePeople: ['p1'] },
  });
  assert.ok(chipsFor(i).length > 0);
  assert.deepEqual(chipsFor(withFiltersCleared(i)), []);
});

// ── People (§13, §45) ───────────────────────────────────────────────────────

test('a person can be included, then excluded, without ending up on both sides', () => {
  // "With Mario" and "without Mario" together is a query that can never match.
  const base = initialIdentity('image').filters;
  const included = togglePerson(base, 'p1', 'include');
  assert.equal(personSide(included, 'p1'), 'include');

  const moved = togglePerson(included, 'p1', 'exclude');
  assert.equal(personSide(moved, 'p1'), 'exclude');
  assert.deepEqual(moved.photo.includePeople, []);
  assert.deepEqual(moved.photo.excludePeople, ['p1']);
});

test('toggling the same side twice removes the person', () => {
  const base = initialIdentity('image').filters;
  const once = togglePerson(base, 'p1', 'include');
  const twice = togglePerson(once, 'p1', 'include');
  assert.equal(personSide(twice, 'p1'), null);
  assert.deepEqual(twice.photo.includePeople, []);
});

test('removing one person leaves the others selected', () => {
  let f = initialIdentity('image').filters;
  for (const id of ['p1', 'p2', 'p3']) f = togglePerson(f, id, 'include');
  f = togglePerson(f, 'p2', 'include');
  assert.deepEqual(f.photo.includePeople, ['p1', 'p3']);
});

test('the match mode switches without disturbing the selection', () => {
  let f = togglePerson(initialIdentity('image').filters, 'p1', 'include');
  f = togglePerson(f, 'p2', 'include');
  const any = withPeopleMode(f, 'any');
  assert.equal(any.photo.includePeopleMode, 'any');
  assert.deepEqual(any.photo.includePeople, ['p1', 'p2']);

  const i = withFilters(initialIdentity('image'), any);
  assert.ok(wire(i).includes('includePeople=p1%2Cp2&includePeopleMode=any'));
});

test('People + other filters all reach the wire together', () => {
  let f = togglePerson(initialIdentity('image').filters, 'p1', 'include');
  f = togglePerson(f, 'p9', 'exclude');
  f = { ...f, common: { ...f.common, favorite: true, minRating: 4 } };
  const q = wire(withFilters(initialIdentity('image'), f));
  for (const part of ['includePeople=p1', 'excludePeople=p9', 'favorite=true', 'minRating=4']) {
    assert.ok(q.includes(part), part);
  }
});

test('the ids needing a label are exactly the referenced ones', () => {
  let f = togglePerson(initialIdentity('image').filters, 'p1', 'include');
  f = togglePerson(f, 'p2', 'exclude');
  assert.deepEqual(referencedPersonIds(f).sort(), ['p1', 'p2']);
});

// ── query generation (§19) ──────────────────────────────────────────────────

test('a filter change starts a new generation; paging does not', () => {
  const i = initialIdentity('image');
  const filtered = withFilters(i, {
    ...i.filters,
    common: { ...i.filters.common, favorite: true },
  });
  assert.notEqual(generationOf(filtered), generationOf(i));
  // Paging keeps the same generation, so the accumulator is not thrown away.
  assert.equal(generationOf(i), generationOf(i));
  assert.ok(wire(i, 'cur-2').includes('cursor=cur-2'));
  assert.ok(!wire(i, null).includes('cursor'));
});

test('changing People starts a new generation, so the cursor is dropped', () => {
  const i = initialIdentity('image');
  const withPerson = withFilters(i, togglePerson(i.filters, 'p1', 'include'));
  assert.notEqual(generationOf(withPerson), generationOf(i));
  // And removing them again returns to the original generation.
  const removed = withFilters(withPerson, togglePerson(withPerson.filters, 'p1', 'include'));
  assert.equal(generationOf(removed), generationOf(i));
});

test('a video-tab filter never rides a photo query', () => {
  const photo = initialIdentity('image');
  const withVideoJunk = withFilters(photo, {
    ...photo.filters,
    video: { ...photo.filters.video, codec: 'h264', hasAudio: true },
  });
  const q = wire(withVideoJunk);
  assert.ok(!q.includes('codec'));
  assert.ok(!q.includes('hasAudio'));
  // And it does not change the photo tab's generation either.
  assert.equal(generationOf(withVideoJunk), generationOf(photo));
});

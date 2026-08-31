// The canonical media query serialization (§42, §46).
//
// Sharing the ListMediaQuery interface would not be enough: two clients can
// agree on a shape and still send different requests. These cases pin the
// WIRE, and every client reaches it through this one function — the per-client
// parity tests then assert that they do.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  MEDIA_LIST_PATH,
  MEDIA_SEMANTIC_PATH,
  albumMediaPath,
  mediaQueryToParams,
  semanticMediaQueryToParams,
  type ListMediaQuery,
} from './index.ts';
import { toQueryString, withQuery } from './query.ts';

const qs = (q: ListMediaQuery) => toQueryString(mediaQueryToParams(q));

test('the empty query sends only the kind', () => {
  assert.equal(qs({ kind: 'all' }), 'kind=all');
  assert.equal(qs({ kind: 'image' }), 'kind=image');
});

test('defaults that mean "no filter" are omitted, not sent', () => {
  // Sending them would make an unfiltered request look filtered in logs,
  // caches and cursors.
  assert.equal(qs({ kind: 'all', scope: 'active' }), 'kind=all');
  assert.equal(qs({ kind: 'all', albumMembership: 'any' }), 'kind=all');
  assert.equal(qs({ kind: 'all', collapseDuplicates: false }), 'kind=all');
  // And the non-default values ARE sent.
  assert.equal(qs({ kind: 'all', scope: 'excluded' }), 'kind=all&scope=excluded');
  assert.equal(
    qs({ kind: 'all', albumMembership: 'unassigned' }),
    'kind=all&albumMembership=unassigned',
  );
});

test('common filters serialize exactly', () => {
  assert.equal(
    qs({
      kind: 'all',
      q: 'mare',
      favorite: true,
      minRating: 4,
      dateTakenFrom: '2026-01-01',
      dateTakenTo: '2026-12-31',
      albumMembership: 'assigned',
      sort: 'datetaken',
      direction: 'desc',
      limit: 60,
      cursor: 'abc',
    }),
    'kind=all&q=mare&favorite=true&minRating=4&dateTakenFrom=2026-01-01'
    + '&dateTakenTo=2026-12-31&albumMembership=assigned&sort=datetaken'
    + '&direction=desc&limit=60&cursor=abc',
  );
});

test('favorite=false is a real filter and is sent', () => {
  // Distinct from "favorite not set": one asks for non-favourites, the other
  // asks for everything. A truthiness check here would silently merge them.
  assert.equal(qs({ kind: 'all', favorite: false }), 'kind=all&favorite=false');
  assert.equal(qs({ kind: 'all', hasAudio: false }), 'kind=all&hasAudio=false');
  assert.equal(qs({ kind: 'all', minRating: 0 }), 'kind=all&minRating=0');
});

test('photo filters, including People', () => {
  assert.equal(
    qs({
      kind: 'image',
      hasGps: true,
      collapseDuplicates: true,
      similarTo: 'file-9',
      includePeople: ['p1', 'p2'],
      excludePeople: ['p3'],
      includePeopleMode: 'all',
    }),
    'kind=image&hasGps=true&collapseDuplicates=true&similarTo=file-9'
    + '&includePeople=p1%2Cp2&excludePeople=p3&includePeopleMode=all',
  );
});

test('People lists are comma-joined ids, and an empty list is not sent', () => {
  assert.equal(
    qs({ kind: 'image', includePeople: ['p1'] }),
    'kind=image&includePeople=p1',
  );
  assert.equal(
    qs({ kind: 'image', includePeople: ['p1', 'p2', 'p3'], includePeopleMode: 'any' }),
    'kind=image&includePeople=p1%2Cp2%2Cp3&includePeopleMode=any',
  );
  // Clearing the last person removes the parameter entirely rather than
  // sending an empty one, which the server would read as a filter.
  assert.equal(qs({ kind: 'image', includePeople: [] }), 'kind=image');
  assert.equal(qs({ kind: 'image', excludePeople: [] }), 'kind=image');
});

test('video filters', () => {
  assert.equal(
    qs({
      kind: 'video',
      durationMin: 10,
      durationMax: 600,
      minHeight: 1080,
      codec: 'h264',
      hasAudio: true,
    }),
    'kind=video&durationMin=10&durationMax=600&minHeight=1080&codec=h264&hasAudio=true',
  );
});

test('a non-finite number is not put on the wire', () => {
  assert.equal(qs({ kind: 'all', minRating: Number.NaN }), 'kind=all');
  assert.equal(qs({ kind: 'video', durationMin: Number.POSITIVE_INFINITY }), 'kind=video');
});

test('values are URL-encoded', () => {
  assert.equal(qs({ kind: 'all', q: 'mare & sole' }), 'kind=all&q=mare%20%26%20sole');
});

test('parameter ORDER is deterministic, so parity is exact equality', () => {
  const a = mediaQueryToParams({ kind: 'all', favorite: true, q: 'x' });
  const b = mediaQueryToParams({ q: 'x', favorite: true, kind: 'all' });
  assert.deepEqual(a, b);
  assert.deepEqual(a.map(([k]) => k), ['kind', 'q', 'favorite']);
});

test('routes are canonical', () => {
  assert.equal(MEDIA_LIST_PATH, '/api/media');
  assert.equal(albumMediaPath('alb-1'), '/api/albums/alb-1/media');
  assert.equal(MEDIA_SEMANTIC_PATH, '/api/media/semantic');
  assert.equal(withQuery('/api/media', []), '/api/media');
  assert.equal(withQuery('/api/media', [['kind', 'all']]), '/api/media?kind=all');
});

test('semantic search keeps its own separate query', () => {
  // A conceptually different backend operation: one definition per operation,
  // not one endpoint for everything (§10).
  assert.equal(
    toQueryString(semanticMediaQueryToParams({ q: 'cane', kind: 'all', limit: 30 })),
    'q=cane&kind=all&limit=30',
  );
  assert.equal(
    toQueryString(semanticMediaQueryToParams({
      q: 'cane', kind: 'image', favorite: true, minRating: 3, albumMembership: 'assigned',
    })),
    'q=cane&kind=image&favorite=true&minRating=3&albumMembership=assigned',
  );
  assert.equal(
    toQueryString(semanticMediaQueryToParams({ q: 'x', kind: 'all', albumMembership: 'any' })),
    'q=x&kind=all',
  );
});

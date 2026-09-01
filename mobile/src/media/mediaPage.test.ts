// The page projection (§4 of the closure): no invented data.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { pageFromListing, pageFromSemantic } from './mediaPage.ts';
import type { MediaItem } from '@nubarca/contracts';

const photo = { id: 'p1', kind: 'image' } as unknown as MediaItem;
const video = { id: 'v1', kind: 'video' } as unknown as MediaItem;

test('a listing page carries the counts the server actually sent', () => {
  const page = pageFromListing({
    items: [photo, video],
    limit: 60, count: 2, nextCursor: 'c2', hasMore: true,
    total: 99, photoCount: 70, videoCount: 29,
  });
  assert.deepEqual(page, {
    items: [photo, video],
    nextCursor: 'c2', hasMore: true, total: 99, photoCount: 70, videoCount: 29,
  });
});

test('a semantic page carries NO counts, because the route produces none', () => {
  // The defect this replaces: photoCount: 0 / videoCount: 0, which are not
  // "unknown" — they are a claim that there were none.
  const page = pageFromSemantic({
    items: [
      { media: photo, bestMatch: {} as never, additionalMatches: [] },
      { media: video, bestMatch: {} as never, additionalMatches: [] },
    ],
    nextCursor: null, hasMore: false, semanticStatus: 'ok', total: 2,
  });
  assert.deepEqual(page.items, [photo, video]);
  assert.equal(page.nextCursor, null);
  assert.equal(page.hasMore, false);
  assert.equal(page.total, 2);
  assert.equal('photoCount' in page, false);
  assert.equal('videoCount' in page, false);
});

test('items, cursor, hasMore and total all survive the semantic projection', () => {
  const page = pageFromSemantic({
    items: [{ media: video, bestMatch: {} as never, additionalMatches: [] }],
    nextCursor: 'rank-7', hasMore: true, semanticStatus: 'ok', total: 41,
  });
  assert.deepEqual(page.items, [video]);
  assert.equal(page.nextCursor, 'rank-7');
  assert.equal(page.hasMore, true);
  assert.equal(page.total, 41);
});

test('the temporal evidence travels BESIDE the item, not inside it', () => {
  // A visual search that finds a video and cannot say which moment matched has
  // answered half the question. The item stays exactly the server's MediaItem.
  const bestMatch = {
    evidenceType: 'visual',
    startMilliseconds: 0, endMilliseconds: 8000, representativeMilliseconds: 4000,
  };
  const page = pageFromSemantic({
    items: [{ media: video, bestMatch, additionalMatches: [] }],
    nextCursor: null, hasMore: false, semanticStatus: 'ok', total: 1,
  });
  assert.deepEqual(page.items[0], video);
  assert.equal('bestMatch' in (page.items[0] as object), false);
  assert.deepEqual(page.evidence?.get('v1'), bestMatch);
});

test('a listing page carries no evidence, because ranking produced none', () => {
  const page = pageFromListing({
    items: [photo], limit: 60, count: 1, nextCursor: null, hasMore: false,
    total: 1, photoCount: 1, videoCount: 0,
  });
  assert.equal(page.evidence, undefined);
});

test('an empty result is empty, not a zero-count claim', () => {
  const page = pageFromSemantic({
    items: [], nextCursor: null, hasMore: false, semanticStatus: 'indexing', total: 0,
  });
  assert.deepEqual(page.items, []);
  assert.equal(page.total, 0);
  assert.equal('photoCount' in page, false);
});

// The People filter contract (§13, §14, §16).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  PEOPLE_LIST_PATH,
  comparePersonSummaries,
  matchesPersonQuery,
  personAvatarPath,
  toPersonSummary,
  type PersonSummary,
} from './people.ts';

const mario = { personId: 'p1', name: 'Mario', faceCount: 12, representative: { faceId: 'f1' } };

test('the summary narrows the owner-private record to identity and label', () => {
  assert.deepEqual(toPersonSummary(mario), {
    personId: 'p1', name: 'Mario', faceCount: 12, representativeFaceId: 'f1',
  });
});

test('nothing about faces, clusters or AI survives the narrowing', () => {
  // §16: the picker must not be able to see management concepts even if the
  // server sends them, so that a future management slice starts from a clean
  // boundary instead of unpicking one.
  const broad = {
    ...mario,
    representative: { faceId: 'f1', box: { x: 0, y: 0, width: 1, height: 1 }, fileItemId: 'file-1' },
    clusterId: 'c-9',
    confidence: 0.93,
    embedding: [0.1, 0.2],
    sessionId: 's-1',
  };
  const summary = toPersonSummary(broad) as PersonSummary & Record<string, unknown>;
  assert.deepEqual(Object.keys(summary).sort(),
    ['faceCount', 'name', 'personId', 'representativeFaceId']);
  for (const leaked of ['clusterId', 'confidence', 'embedding', 'sessionId', 'box']) {
    assert.equal(summary[leaked], undefined, leaked);
  }
});

test('an unnamed person keeps a usable identity', () => {
  assert.deepEqual(
    toPersonSummary({ personId: 'p2', name: null, faceCount: 3, representative: null }),
    { personId: 'p2', name: null, faceCount: 3, representativeFaceId: null },
  );
});

test('picker order is stable: named alphabetically, then unnamed by size', () => {
  const people: PersonSummary[] = [
    { personId: 'c', name: null, faceCount: 2, representativeFaceId: null },
    { personId: 'b', name: 'Laura', faceCount: 1, representativeFaceId: null },
    { personId: 'd', name: null, faceCount: 9, representativeFaceId: null },
    { personId: 'a', name: 'Aldo', faceCount: 5, representativeFaceId: null },
  ];
  assert.deepEqual(
    [...people].sort(comparePersonSummaries).map((p) => p.personId),
    ['a', 'b', 'd', 'c'],
  );
});

test('search matches display names, case-insensitively, and empty matches all', () => {
  const p: PersonSummary = { personId: 'p1', name: 'Mario', faceCount: 1, representativeFaceId: null };
  for (const q of ['mar', 'MARIO', ' mario ', '']) {
    assert.equal(matchesPersonQuery(p, q), true, q);
  }
  assert.equal(matchesPersonQuery(p, 'laura'), false);
  const unnamed: PersonSummary = { personId: 'p2', name: null, faceCount: 1, representativeFaceId: null };
  assert.equal(matchesPersonQuery(unnamed, 'mario'), false);
  assert.equal(matchesPersonQuery(unnamed, ''), true);
});

test('routes are canonical and the avatar id is escaped', () => {
  assert.equal(PEOPLE_LIST_PATH, '/api/people');
  assert.equal(personAvatarPath('f1'), '/api/people/faces/f1/preview');
  assert.equal(personAvatarPath('a/b'), '/api/people/faces/a%2Fb/preview');
});

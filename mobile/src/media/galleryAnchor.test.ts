import { test } from 'node:test';
import assert from 'node:assert/strict';
import { indexOfItemId } from './galleryAnchor.ts';

const items = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];
const key = (item: { id: string }): string => item.id;

test('finds an item by id', () => {
  assert.equal(indexOfItemId(items, key, 'a'), 0);
  assert.equal(indexOfItemId(items, key, 'c'), 2);
});

test('a null anchor is not a position', () => {
  assert.equal(indexOfItemId(items, key, null), -1);
});

test('an id that is not present yet is not a position', () => {
  // A viewer return can name an item that this page has not loaded. Answering
  // -1 lets the caller keep the anchor armed instead of scrolling somewhere
  // arbitrary.
  assert.equal(indexOfItemId(items, key, 'zzz'), -1);
});

test('the id is resolved through the caller\'s key, not a fixed field', () => {
  // The shared album keys on albumItemId, not on id: one list primitive means
  // one anchor helper, so it cannot assume either shape.
  const shared = [{ albumItemId: 'x' }, { albumItemId: 'y' }];
  assert.equal(indexOfItemId(shared, (i) => i.albumItemId, 'y'), 1);
});

test('empty list', () => {
  assert.equal(indexOfItemId([], key, 'a'), -1);
});

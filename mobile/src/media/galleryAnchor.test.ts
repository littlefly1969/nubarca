import { test } from 'node:test';
import assert from 'node:assert/strict';
import { indexOfItemId } from './galleryAnchor.ts';

const items = [{ id: 'a' }, { id: 'b' }, { id: 'c' }];

test('finds an item by id', () => {
  assert.equal(indexOfItemId(items, 'a'), 0);
  assert.equal(indexOfItemId(items, 'c'), 2);
});

test('a null anchor is not a position', () => {
  assert.equal(indexOfItemId(items, null), -1);
});

test('an id that is not present yet is not a position', () => {
  // A viewer return can name an item that this page has not loaded. Answering
  // -1 lets the caller keep the anchor armed instead of scrolling somewhere
  // arbitrary.
  assert.equal(indexOfItemId(items, 'zzz'), -1);
});

test('empty list', () => {
  assert.equal(indexOfItemId([], 'a'), -1);
});

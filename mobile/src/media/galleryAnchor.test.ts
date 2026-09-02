import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { anchorFromVisible, anchorIndexOf, rowOf } from './galleryAnchor.ts';

const items = ['a', 'b', 'c', 'd', 'e', 'f', 'g'].map((id) => ({ id }));

test('an anchor is found by identity, wherever it has moved to', () => {
  assert.equal(anchorIndexOf(items, 'a'), 0);
  assert.equal(anchorIndexOf(items, 'e'), 4);
});

test('a missing anchor is an ordinary answer, not a failure', () => {
  // A refresh or a filter change can legitimately remove the item somebody was
  // looking at. The gallery should stay where it is, not throw and not jump.
  assert.equal(anchorIndexOf(items, 'zz'), null);
  assert.equal(anchorIndexOf([], 'a'), null);
  assert.equal(anchorIndexOf(items, null), null);
});

test('the row follows the geometry in force, not the one that was', () => {
  // This is the whole reason the anchor is an id: item 4 is on row 1 at three
  // columns and row 0 at five, and a pixel offset cannot know that.
  assert.equal(rowOf(4, 3), 1);
  assert.equal(rowOf(4, 5), 0);
  assert.equal(rowOf(11, 4), 2);
  assert.equal(rowOf(0, 3), 0);
});

test('a nonsensical column count does not produce a nonsensical row', () => {
  assert.equal(rowOf(7, 0), 0);
  assert.equal(rowOf(7, -3), 0);
});

test('the anchor is the FIRST visible item', () => {
  // Where the eye is at the top of the viewport. Anchoring on the middle puts
  // the screen back half a row off from where it was left.
  assert.equal(anchorFromVisible(['c', 'd', 'e']), 'c');
  assert.equal(anchorFromVisible([]), null);
});

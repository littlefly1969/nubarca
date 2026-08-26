import assert from 'node:assert/strict';
import test from 'node:test';
import { IdSelection } from './selection.ts';

test('toggle selects then deselects', () => {
  const s = new IdSelection();
  s.toggle('a');
  assert.equal(s.size, 1);
  assert.equal(s.has('a'), true);
  s.toggle('a');
  assert.equal(s.size, 0);
});

test('selectMany accumulates; clear resets', () => {
  const s = new IdSelection();
  s.selectMany(['x', 'y']);
  s.selectMany(['y', 'z']);
  assert.deepEqual(s.values().sort(), ['x', 'y', 'z']);
  s.clear();
  assert.equal(s.size, 0);
});

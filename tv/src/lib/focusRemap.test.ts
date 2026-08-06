import assert from 'node:assert/strict';
import test from 'node:test';
import { remapFocusIndexById } from './focusRemap.ts';

const items = (...ids: string[]) => ids.map((id) => ({ id }));

test('keeps focus on the same item when it still exists at a new index', () => {
  const prev = items('a', 'b', 'c');
  const next = items('z', 'a', 'b', 'c'); // a live upload prepended
  assert.equal(remapFocusIndexById(prev, 1, next), 2); // was 'b' → now index 2
});

test('falls back to the first item when the focused item is gone', () => {
  const prev = items('a', 'b', 'c');
  const next = items('x', 'y'); // face filter swapped the list
  assert.equal(remapFocusIndexById(prev, 2, next), 0);
});

test('returns 0 for an empty next list', () => {
  assert.equal(remapFocusIndexById(items('a', 'b'), 1, []), 0);
});

test('clamps a stale previous index defensively', () => {
  const prev = items('a', 'b');
  const next = items('a', 'b');
  assert.equal(remapFocusIndexById(prev, 99, next), 1); // clamp to 'b'
});

test('handles an empty previous list', () => {
  assert.equal(remapFocusIndexById([], 0, items('a', 'b')), 0);
});

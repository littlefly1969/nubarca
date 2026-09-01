import assert from 'node:assert/strict';
import { test } from 'node:test';
import { shouldRefreshOnFocus } from './focusRefresh.ts';

test('an empty list loads on focus', () => {
  assert.equal(shouldRefreshOnFocus({ itemCount: 0, stale: false }), true);
});

test('a list with content KEEPS it, so the reader keeps their place', () => {
  // Refreshing replaces the accumulator with page one, which is what dropped
  // the user back up the library on every return from the viewer.
  assert.equal(shouldRefreshOnFocus({ itemCount: 60, stale: false }), false);
  assert.equal(shouldRefreshOnFocus({ itemCount: 5000, stale: false }), false);
});

test('an explicit staleness signal still wins', () => {
  // A mutation made elsewhere must be able to force the reload.
  assert.equal(shouldRefreshOnFocus({ itemCount: 500, stale: true }), true);
});

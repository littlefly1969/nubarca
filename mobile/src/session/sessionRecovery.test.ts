// Session-recovery policy tests: the cold-start decision that separates a
// DEAD cookie from an UNREACHABLE server. Dropping the persisted cookie on a
// mere network failure would sign an offline user out permanently.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { shouldDropPersistedSession } from './sessionRecovery.ts';

test('a 401 verdict drops the persisted cookie', () => {
  const err = Object.assign(new Error('gone'), { status: 401 });
  assert.equal(shouldDropPersistedSession(err), true);
});

test('a 403 verdict drops the persisted cookie', () => {
  const err = Object.assign(new Error('forbidden'), { status: 403 });
  assert.equal(shouldDropPersistedSession(err), true);
});

test('a server error (5xx) keeps the cookie', () => {
  const err = Object.assign(new Error('boom'), { status: 503 });
  assert.equal(shouldDropPersistedSession(err), false);
});

test('a network TypeError (no status) keeps the cookie', () => {
  const err = new TypeError('Network request failed');
  assert.equal(shouldDropPersistedSession(err), false);
});

test('an error-shaped object without status keeps the cookie', () => {
  assert.equal(shouldDropPersistedSession({}), false);
});

test('null/undefined errors keep the cookie', () => {
  assert.equal(shouldDropPersistedSession(null), false);
  assert.equal(shouldDropPersistedSession(undefined), false);
});

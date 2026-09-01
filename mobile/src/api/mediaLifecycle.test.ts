// Selection lifecycle transport (§21, §24, §48).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { applyToSelection } from './mediaLifecycle.ts';

test('a fully successful run reports every item', async () => {
  const done: string[] = [];
  const result = await applyToSelection(['a', 'b', 'c'], async (id) => { done.push(id); });
  assert.deepEqual(done, ['a', 'b', 'c']);
  assert.deepEqual(result, { requested: 3, succeeded: 3, failed: 0 });
});

test('a partial failure is COUNTED, not swallowed and not fatal', async () => {
  // The alternative — stopping at the first error — leaves the user with no
  // idea how much was applied; claiming success is worse still.
  const result = await applyToSelection(['a', 'bad', 'c'], async (id) => {
    if (id === 'bad') throw new Error('nope');
  });
  assert.deepEqual(result, { requested: 3, succeeded: 2, failed: 1 });
});

test('every item failing is reported as such, without throwing', async () => {
  const result = await applyToSelection(['a', 'b'], async () => { throw new Error('down'); });
  assert.deepEqual(result, { requested: 2, succeeded: 0, failed: 2 });
});

test('an empty selection performs nothing', async () => {
  let called = 0;
  const result = await applyToSelection([], async () => { called += 1; });
  assert.equal(called, 0);
  assert.deepEqual(result, { requested: 0, succeeded: 0, failed: 0 });
});

test('cancellation stops the loop and reports only what really happened', async () => {
  // Never claim more than was done: the count is what the UI will say.
  const controller = new AbortController();
  const done: string[] = [];
  const result = await applyToSelection(['a', 'b', 'c', 'd'], async (id) => {
    done.push(id);
    if (id === 'b') controller.abort();
  }, controller.signal);
  assert.deepEqual(done, ['a', 'b']);
  assert.equal(result.succeeded, 2);
  assert.equal(result.requested, 4);
});

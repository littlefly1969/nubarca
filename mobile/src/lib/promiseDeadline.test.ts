import assert from 'node:assert/strict';
import { test } from 'node:test';
import { withDeadline } from './promiseDeadline.ts';

test('returns a native result that settles before the deadline', async () => {
  assert.equal(await withDeadline(Promise.resolve('ok'), 50), 'ok');
});

test('preserves a native rejection', async () => {
  const failure = new Error('native failure');
  await assert.rejects(withDeadline(Promise.reject(failure), 50), failure);
});

test('rejects a native operation that never settles', async () => {
  await assert.rejects(
    withDeadline(new Promise<never>(() => undefined), 5, 'secure read timeout'),
    /secure read timeout/,
  );
});

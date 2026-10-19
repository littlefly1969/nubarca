import assert from 'node:assert/strict';
import test from 'node:test';
import { TvSelectionStore, type TvSelectionChange } from './tvSelection.ts';

test('OK toggles, the count follows, and every change notifies once', () => {
  const store = new TvSelectionStore();
  let notified = 0;
  store.subscribe(() => { notified += 1; });
  assert.equal(store.toggle('a'), true);
  assert.equal(store.toggle('b'), true);
  assert.equal(store.toggle('a'), false);
  assert.deepEqual(store.list(), ['b']);
  assert.equal(store.count, 1);
  assert.equal(notified, 3);
  store.clear();
  store.clear();
  assert.equal(notified, 4);
});

test('local changes reach a mirror; adopted ones are not echoed', () => {
  const store = new TvSelectionStore();
  const changes: TvSelectionChange[] = [];
  store.onChange((c) => changes.push(c));
  store.toggle('a');
  store.adopt(['x', 'y', 'x']);
  store.toggle('x');
  assert.deepEqual(store.list(), ['y']);
  assert.deepEqual(changes, [{ kind: 'add', ids: ['a'] }, { kind: 'remove', ids: ['x'] }]);
});

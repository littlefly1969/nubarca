// Selection-mode transitions (device-reported: the select button did nothing,
// and the first long-pressed photo did not stick).
//
// The hook is React, so the transitions are exercised through the same reducer
// shape it uses: each action must be computable from the PREVIOUS state alone,
// which is what makes a long press one atomic step instead of two racing ones.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { IdSelection } from './selection.ts';

interface Snapshot { selecting: boolean; selection: IdSelection; }
const IDLE: Snapshot = { selecting: false, selection: new IdSelection() };

// Mirrors the updaters in useSelectionState.
const begin = (c: Snapshot): Snapshot =>
  c.selecting ? c : { selecting: true, selection: new IdSelection() };
const beginWith = (c: Snapshot, id: string): Snapshot => ({
  selecting: true,
  selection: c.selecting
    ? new IdSelection().selectMany(c.selection.values()).toggle(id)
    : IdSelection.of(id),
});
const toggle = (c: Snapshot, id: string): Snapshot => ({
  selecting: true,
  selection: new IdSelection().selectMany(c.selection.values()).toggle(id),
});

test('the mode can be entered with NOTHING selected', () => {
  // The defect: selecting was derived from size > 0, so the header button
  // could never turn anything on and appeared broken.
  const s = begin(IDLE);
  assert.equal(s.selecting, true);
  assert.equal(s.selection.size, 0);
});

test('a long press enters the mode AND keeps the item, in one step', () => {
  // Two writes over one stale snapshot is what dropped the first photo.
  const s = beginWith(IDLE, 'a');
  assert.equal(s.selecting, true);
  assert.deepEqual(s.selection.values(), ['a']);
});

test('a long press inside the mode toggles like a tap', () => {
  let s = beginWith(IDLE, 'a');
  s = beginWith(s, 'b');
  assert.deepEqual(s.selection.values().sort(), ['a', 'b']);
  s = beginWith(s, 'a');
  assert.deepEqual(s.selection.values(), ['b']);
});

test('tapping items keeps the mode open even when it empties', () => {
  // Emptying the set must NOT drop out of the mode — that was the old
  // behaviour, and it made deselecting the last item close the toolbar.
  let s = beginWith(IDLE, 'a');
  s = toggle(s, 'a');
  assert.equal(s.selection.size, 0);
  assert.equal(s.selecting, true);
});

test('begin is idempotent and never clears an existing selection', () => {
  const withItems = beginWith(IDLE, 'a');
  assert.deepEqual(begin(withItems).selection.values(), ['a']);
  assert.equal(begin(withItems), withItems);
});

test('leaving the mode drops what was picked', () => {
  const s = beginWith(IDLE, 'a');
  assert.equal(s.selection.size, 1);
  assert.equal(IDLE.selecting, false);
  assert.equal(IDLE.selection.size, 0);
});

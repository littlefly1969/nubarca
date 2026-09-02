import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  HIDE_DISTANCE,
  REVEAL_DISTANCE,
  initialGalleryChromeState,
  nextGalleryChromeState,
  type GalleryChromeState,
} from './galleryChrome.ts';

/** Drive the rule with a sequence of offsets, as a scroll would. */
function scroll(offsets: number[], from = initialGalleryChromeState): GalleryChromeState {
  return offsets.reduce(nextGalleryChromeState, from);
}

test('the chrome starts visible and stays visible at the top', () => {
  assert.equal(initialGalleryChromeState.hidden, false);
  assert.equal(scroll([0, 2, 5, 0]).hidden, false);
});

test('travelling into the content hides it, but not on a nudge', () => {
  assert.equal(scroll([0, 10, 20]).hidden, false, 'hid on a nudge');
  assert.equal(scroll([0, HIDE_DISTANCE + 1]).hidden, true);
});

test('reversing brings it back without returning to the top', () => {
  const deep = scroll([0, 400]);
  assert.equal(deep.hidden, true);
  const reversed = nextGalleryChromeState(deep, 400 - REVEAL_DISTANCE);
  assert.equal(reversed.hidden, false, 'did not reveal on a deliberate reversal');
  assert.ok(400 - REVEAL_DISTANCE > 0, 'and did so far from offset zero');
});

test('a shaky thumb does not flicker the bar', () => {
  // The failure this prevents: comparing against the previous frame makes any
  // one-pixel wobble a direction change, and the bar strobes.
  let state = scroll([0, 400]);
  const jitter = [400, 399, 400, 401, 398, 402, 399, 400, 397, 401];
  for (const y of jitter) {
    const before = state.hidden;
    state = nextGalleryChromeState(state, y);
    assert.equal(state.hidden, before, `flickered at ${y}`);
  }
});

test('the anchor follows the deepest point, so a reveal is measured from the turn', () => {
  // Scroll far in, then turn around: the reveal must answer the turn, not the
  // distance from wherever the bar happened to hide.
  let state = scroll([0, 100, 300, 900]);
  assert.equal(state.anchorY, 900);
  state = nextGalleryChromeState(state, 900 - REVEAL_DISTANCE + 1);
  assert.equal(state.hidden, true, 'revealed before the reversal was meaningful');
  state = nextGalleryChromeState(state, 900 - REVEAL_DISTANCE);
  assert.equal(state.hidden, false);
});

test('returning to the top always settles visible, even mid-hide', () => {
  const state = scroll([0, 600, 300, 0]);
  assert.equal(state.hidden, false);
  assert.equal(state.anchorY, 0);
});

test('the rule is a pure function of state and offset', () => {
  const state = { hidden: true, anchorY: 500 } as const;
  const a = nextGalleryChromeState(state, 480);
  const b = nextGalleryChromeState(state, 480);
  assert.deepEqual(a, b);
  assert.deepEqual(state, { hidden: true, anchorY: 500 }, 'the input state was mutated');
});

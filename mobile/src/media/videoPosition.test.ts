// Video position across a remount (device-reported: rotation restarted videos).

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  forgetAllPositions,
  forgetPosition,
  recallPosition,
  rememberPosition,
  restorablePosition,
} from './videoPosition.ts';

const A = 'https://unit.test/api/files/a/video';
const B = 'https://unit.test/api/files/b/video';

test('a remembered position comes back for the same source', () => {
  forgetAllPositions();
  rememberPosition(A, 42);
  assert.deepEqual(recallPosition(A), { source: A, positionSeconds: 42 });
  assert.equal(recallPosition(B), null);
});

test('a position is NEVER restored onto a different video', () => {
  // Seeking one video to another's timestamp is worse than starting over, and
  // it is what a naive "restore the last position" does after changing item.
  forgetAllPositions();
  rememberPosition(A, 42);
  assert.equal(restorablePosition(recallPosition(A), B), null);
  assert.equal(restorablePosition({ source: A, positionSeconds: 42 }, B), null);
});

test('the same source restores', () => {
  assert.equal(restorablePosition({ source: A, positionSeconds: 42 }, A), 42);
});

test('nonsense is refused rather than seeked to', () => {
  for (const bad of [Number.NaN, Number.POSITIVE_INFINITY, -5]) {
    assert.equal(restorablePosition({ source: A, positionSeconds: bad }, A), null, String(bad));
    rememberPosition(A, bad);
  }
  // And a bad value is not even stored.
  forgetAllPositions();
  rememberPosition(A, Number.NaN);
  assert.equal(recallPosition(A), null);
});

test('a position at or past the end starts over instead', () => {
  // Restoring there shows the last frame and reports the video as finished
  // immediately — which in a sequence means advancing the moment it opens.
  assert.equal(restorablePosition({ source: A, positionSeconds: 90 }, A, 90), null);
  assert.equal(restorablePosition({ source: A, positionSeconds: 89.9 }, A, 90), null);
  assert.equal(restorablePosition({ source: A, positionSeconds: 60 }, A, 90), 60);
});

test('a position at the very start is not worth a seek', () => {
  assert.equal(restorablePosition({ source: A, positionSeconds: 0 }, A), null);
  assert.equal(restorablePosition({ source: A, positionSeconds: 0.2 }, A), null);
  assert.equal(restorablePosition({ source: A, positionSeconds: 1.5 }, A), 1.5);
});

test('an unknown duration does not block a restore', () => {
  assert.equal(restorablePosition({ source: A, positionSeconds: 30 }, A, null), 30);
  assert.equal(restorablePosition({ source: A, positionSeconds: 30 }, A, 0), 30);
});

test('forgetting is per-source and wholesale, for identity changes', () => {
  forgetAllPositions();
  rememberPosition(A, 10);
  rememberPosition(B, 20);
  forgetPosition(A);
  assert.equal(recallPosition(A), null);
  assert.notEqual(recallPosition(B), null);
  forgetAllPositions();
  assert.equal(recallPosition(B), null);
});

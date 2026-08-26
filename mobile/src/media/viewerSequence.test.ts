// Viewer-sequence privacy tests (acceptance BLOCKER 6): the pure model behind
// ViewerProvider. Cross-account rule: after a reset, NOTHING of the previous
// user's sequence — items, keys, metadata — is reachable from this object.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ViewerSequenceModel, type ViewerSlide } from './viewerSequence.ts';

function slide(key: string): ViewerSlide {
  return {
    key,
    kind: 'image',
    displayName: `name-${key}`,
    imagePath: `/p/${key}`,
    videoSource: null,
    posterUrl: null,
  };
}

test('open replaces EVERYTHING from a previous sequence', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1'), slide('a2')], 'a1');
  m.open([slide('b1')], 'b1');
  const snap = m.snapshot();
  assert.equal(snap?.slides.length, 1);
  assert.deepEqual(
    snap.slides.map((s) => s.key),
    ['b1'],
  );
});

test('reset wipes the whole sequence — no residue for the next account', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1'), slide('a2')], 'a2');
  m.reset(); // logout / account switch
  assert.equal(m.snapshot(), null);

  // User B opens their own album: zero keys from user A anywhere.
  m.open([slide('b1')], 'b1');
  const keys = m.snapshot()?.slides.map((s) => s.key) ?? [];
  assert.ok(keys.every((k) => !k.startsWith('a')));
});

test('close() really zeroes the state, including any persistent ref', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1')], 'a1');
  m.close();
  assert.equal(m.snapshot(), null);
  // close is idempotent.
  m.close();
  assert.equal(m.snapshot(), null);
});

test('setIndex moves within bounds and is ignored without a sequence', () => {
  const m = new ViewerSequenceModel();
  m.setIndex(0); // no sequence yet — must be a safe no-op
  m.open([slide('a1'), slide('a2')], 'a1');
  m.setIndex(1);
  assert.equal(m.snapshot()?.index, 1);
  m.setIndex(99); // out of bounds — ignored
  assert.equal(m.snapshot()?.index, 1);
  m.setIndex(-1); // out of bounds — ignored
  assert.equal(m.snapshot()?.index, 1);
});

test('opening an EMPTY sequence resets instead of mounting garbage', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1')], 'a1');
  m.open([], 'x');
  assert.equal(m.snapshot(), null);
});

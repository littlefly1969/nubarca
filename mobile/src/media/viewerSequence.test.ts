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

/** A sequence from a list of keys. */
const keys = (ks: string[]): ViewerSlide[] => ks.map(slide);

// --- NUBARCA-UX-01.1 §4: the viewer's current item, and the way back --------

test('focus follows the index, so the viewer knows what it is showing', () => {
  // It used to stay on whatever was opened. After swiping from item 24 to 29
  // the viewer still believed it was showing 24 — and the gallery, on the way
  // back, returned to the wrong photo.
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b', 'c', 'd']), 'a');
  model.setIndex(2);
  const snapshot = model.snapshot()!;
  assert.equal(snapshot.index, 2);
  assert.equal(snapshot.focusedKey, 'c');
  assert.equal(snapshot.focusedKey, snapshot.slides[snapshot.index].key);
});

test('opening already satisfies the invariant', () => {
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b', 'c']), 'b');
  const snapshot = model.snapshot()!;
  assert.equal(snapshot.focusedKey, snapshot.slides[snapshot.index].key);
});

test('an invalid index moves neither the index nor the focus', () => {
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b']), 'a');
  for (const bad of [-1, 2, 99]) {
    model.setIndex(bad);
    const snapshot = model.snapshot()!;
    assert.equal(snapshot.index, 0, `index moved on ${bad}`);
    assert.equal(snapshot.focusedKey, 'a', `focus moved on ${bad}`);
  }
});

test('closing hands back the item on screen, and drops the slides', () => {
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b', 'c']), 'a');
  model.setIndex(2);
  model.close();
  assert.equal(model.snapshot(), null, 'the sequence survived the close');
  assert.equal(model.takeReturnAnchor(), 'c');
});

test('the return anchor is one-shot', () => {
  // A second visit to the same gallery, without a second viewer visit, must
  // not be yanked back to something somebody looked at once.
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b']), 'b');
  model.close();
  assert.equal(model.takeReturnAnchor(), 'b');
  assert.equal(model.takeReturnAnchor(), null);
});

test('an account boundary keeps nothing at all', () => {
  // A pending return anchor is one account's browsing position.
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b']), 'b');
  model.close();
  model.reset();
  assert.equal(model.takeReturnAnchor(), null);
  assert.equal(model.snapshot(), null);
});

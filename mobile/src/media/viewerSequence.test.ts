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
  m.open([slide('a1'), slide('a2')], 'a1', 'photos');
  m.open([slide('b1')], 'b1', 'photos');
  const snap = m.snapshot();
  assert.equal(snap?.slides.length, 1);
  assert.deepEqual(
    snap.slides.map((s) => s.key),
    ['b1'],
  );
});

test('reset wipes the whole sequence — no residue for the next account', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1'), slide('a2')], 'a2', 'photos');
  m.reset(); // logout / account switch
  assert.equal(m.snapshot(), null);

  // User B opens their own album: zero keys from user A anywhere.
  m.open([slide('b1')], 'b1', 'photos');
  const keys = m.snapshot()?.slides.map((s) => s.key) ?? [];
  assert.ok(keys.every((k) => !k.startsWith('a')));
});

test('close() really zeroes the state, including any persistent ref', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1')], 'a1', 'photos');
  m.close();
  assert.equal(m.snapshot(), null);
  // close is idempotent.
  m.close();
  assert.equal(m.snapshot(), null);
});

test('setIndex moves within bounds and is ignored without a sequence', () => {
  const m = new ViewerSequenceModel();
  m.setIndex(0); // no sequence yet — must be a safe no-op
  m.open([slide('a1'), slide('a2')], 'a1', 'photos');
  m.setIndex(1);
  assert.equal(m.snapshot()?.index, 1);
  m.setIndex(99); // out of bounds — ignored
  assert.equal(m.snapshot()?.index, 1);
  m.setIndex(-1); // out of bounds — ignored
  assert.equal(m.snapshot()?.index, 1);
});

test('opening an EMPTY sequence resets instead of mounting garbage', () => {
  const m = new ViewerSequenceModel();
  m.open([slide('a1')], 'a1', 'photos');
  m.open([], 'x', 'photos');
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
  model.open(keys(['a', 'b', 'c', 'd']), 'a', 'photos');
  model.setIndex(2);
  const snapshot = model.snapshot()!;
  assert.equal(snapshot.index, 2);
  assert.equal(snapshot.focusedKey, 'c');
  assert.equal(snapshot.focusedKey, snapshot.slides[snapshot.index].key);
});

test('opening already satisfies the invariant', () => {
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b', 'c']), 'b', 'photos');
  const snapshot = model.snapshot()!;
  assert.equal(snapshot.focusedKey, snapshot.slides[snapshot.index].key);
});

test('an invalid index moves neither the index nor the focus', () => {
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b']), 'a', 'photos');
  for (const bad of [-1, 2, 99]) {
    model.setIndex(bad);
    const snapshot = model.snapshot()!;
    assert.equal(snapshot.index, 0, `index moved on ${bad}`);
    assert.equal(snapshot.focusedKey, 'a', `focus moved on ${bad}`);
  }
});

test('closing hands back the item on screen, and drops the slides', () => {
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b', 'c']), 'a', 'photos');
  model.setIndex(2);
  model.close();
  assert.equal(model.snapshot(), null, 'the sequence survived the close');
  assert.equal(model.takeReturnPosition('photos')?.focusedKey ?? null, 'c');
});

test('the return anchor is one-shot', () => {
  // A second visit to the same gallery, without a second viewer visit, must
  // not be yanked back to something somebody looked at once.
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b']), 'b', 'photos');
  model.close();
  assert.equal(model.takeReturnPosition('photos')?.focusedKey ?? null, 'b');
  assert.equal(model.takeReturnPosition('photos')?.focusedKey ?? null, null);
});

test('an account boundary keeps nothing at all', () => {
  // A pending return anchor is one account's browsing position.
  const model = new ViewerSequenceModel();
  model.open(keys(['a', 'b']), 'b', 'photos');
  model.close();
  model.reset();
  assert.equal(model.takeReturnPosition('photos')?.focusedKey ?? null, null);
  assert.equal(model.snapshot(), null);
});

// UX-01.5: the sequence has to be able to GROW, because the gallery that
// opened it keeps paginating while the user swipes.

const seq = (n: number, from = 0): ViewerSlide[] =>
  Array.from({ length: n }, (_, i) => slide(`item-${from + i}`));

test('an append extends the sequence without moving the user', () => {
  const model = new ViewerSequenceModel();
  model.open(seq(60), 'item-55', 'photos');
  model.setIndex(55);
  assert.equal(model.appendSlides(seq(120)), true);
  const after = model.snapshot()!;
  assert.equal(after.slides.length, 120);
  // The whole point: the counter grows, the reader does not move.
  assert.equal(after.index, 55);
  assert.equal(after.focusedKey, 'item-55');
  assert.equal(after.openedKey, 'item-55');
  assert.equal(after.scopeKey, 'photos');
});

test('an append keeps the slide objects that are already mounted', () => {
  // Replacing them would remount every mounted slide — including the video
  // playing on screen right now.
  const model = new ViewerSequenceModel();
  const first = seq(3);
  model.open(first, 'item-0', 'photos');
  const before = model.snapshot()!.slides[1];
  model.appendSlides(seq(5));
  assert.equal(model.snapshot()!.slides[1], before, 'slide 1 was rebuilt');
});

test('a page that brings nothing new changes nothing', () => {
  const model = new ViewerSequenceModel();
  model.open(seq(60), 'item-10', 'photos');
  assert.equal(model.appendSlides(seq(60)), false, 'duplicates must not append');
  assert.equal(model.snapshot()!.slides.length, 60);
  // Answering false is what lets the provider leave React alone.
});

test('the continuation may hand back the WHOLE accumulated set', () => {
  // §8: the gallery returns everything it has; only unseen keys are taken.
  const model = new ViewerSequenceModel();
  model.open(seq(60), 'item-0', 'photos');
  model.appendSlides(seq(120));
  model.appendSlides(seq(172));
  const keys = model.snapshot()!.slides.map((s) => s.key);
  assert.equal(keys.length, 172);
  assert.equal(new Set(keys).size, 172, 'a key was appended twice');
  assert.equal(keys[0], 'item-0');
  assert.equal(keys[171], 'item-171');
});

test('a page landing after close does not resurrect the sequence', () => {
  // The request the user left behind still completes. It must find nothing.
  const model = new ViewerSequenceModel();
  model.open(seq(60), 'item-0', 'photos');
  model.close();
  assert.equal(model.appendSlides(seq(120)), false);
  assert.equal(model.snapshot(), null);
  // And the return position the close recorded is untouched by the late page.
  assert.equal(model.takeReturnPosition('photos')?.focusedKey, 'item-0');
});

test('a page landing after an account switch does not resurrect it either', () => {
  const model = new ViewerSequenceModel();
  model.open(seq(60), 'item-0', 'photos');
  model.reset();
  assert.equal(model.appendSlides(seq(120)), false);
  assert.equal(model.snapshot(), null);
});

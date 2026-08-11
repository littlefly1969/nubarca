import { describe, expect, it } from 'vitest';
import { withoutFace } from './faceViewerSequence';

// The viewer is CONTROLLED, so this is where "the face I was looking at is gone"
// is decided — once, for every People surface. The bug it closes: after
// ignoring a face the viewer stayed on its id and kept asking the server for a
// face that is no longer a candidate.

describe('withoutFace', () => {
  it('advances to the face that took the removed one\'s place', () => {
    const seq = { faceIds: ['a', 'b', 'c'], index: 1 };
    expect(withoutFace(seq, 'b')).toEqual({ faceIds: ['a', 'c'], index: 1 }); // now 'c'
  });

  it('steps back when the removed face was the last one', () => {
    const seq = { faceIds: ['a', 'b', 'c'], index: 2 };
    expect(withoutFace(seq, 'c')).toEqual({ faceIds: ['a', 'b'], index: 1 });
  });

  it('keeps the position when an earlier face is removed', () => {
    const seq = { faceIds: ['a', 'b', 'c'], index: 2 };
    expect(withoutFace(seq, 'a')).toEqual({ faceIds: ['b', 'c'], index: 1 });
  });

  it('closes the viewer when nothing is left to look at', () => {
    expect(withoutFace({ faceIds: ['a'], index: 0 }, 'a')).toBeNull();
    expect(withoutFace(null, 'a')).toBeNull();
  });

  it('leaves a sequence that does not contain the face alone', () => {
    const seq = { faceIds: ['a', 'b'], index: 1 };
    expect(withoutFace(seq, 'zzz')).toEqual(seq);
  });
});

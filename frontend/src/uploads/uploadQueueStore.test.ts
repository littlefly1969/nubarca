import { describe, expect, it } from 'vitest';
import { fileKey, partition } from './uploadQueueStore';

/**
 * WHAT MAKES A RESUME A RESUME.
 *
 * The IndexedDB half is exercised by the page; what is worth pinning down here
 * is the identity rule, because everything else rests on it: if `fileKey`
 * changed shape, a visitor re-picking the same photographs would upload all of
 * them again and nobody would notice until a phone had sent forty files twice.
 */
describe('recognising a file somebody already sent', () => {
  function file(name: string, size: number, lastModified: number): File {
    // A real File, because the key reads three of its properties and a stub
    // that happened to have them would prove nothing about the real one.
    const blob = new File([new Uint8Array(size)], name, { lastModified });
    return blob;
  }

  it('is the same file when name, size and stamp agree', () => {
    expect(fileKey(file('IMG_1.jpg', 12, 1000)))
      .toBe(fileKey(file('IMG_1.jpg', 12, 1000)));
  });

  it('is a different file when any of the three differs', () => {
    const base = fileKey(file('IMG_1.jpg', 12, 1000));
    expect(fileKey(file('IMG_2.jpg', 12, 1000))).not.toBe(base);
    expect(fileKey(file('IMG_1.jpg', 13, 1000))).not.toBe(base);
    expect(fileKey(file('IMG_1.jpg', 12, 1001))).not.toBe(base);
  });

  it('cannot be confused by a name that contains the separator', () => {
    // The parts are joined with NUL, which a file name cannot contain — so
    // "a\u0000b" and "a" + "b" can never collide into one key.
    expect(fileKey(file('a', 1, 1))).not.toBe(fileKey(file('a\u00001', 1, 1)));
  });

  it('splits a fresh pick into what still has to go and what already went', () => {
    const done = new Set([fileKey(file('done.jpg', 5, 7))]);
    const { pending, skipped } = partition(
      [file('done.jpg', 5, 7), file('new.jpg', 5, 7)], done);

    expect(skipped.map((f) => f.name)).toEqual(['done.jpg']);
    expect(pending.map((f) => f.name)).toEqual(['new.jpg']);
  });

  it('sends everything when nothing is known, which is the first visit', () => {
    const { pending, skipped } = partition(
      [file('a.jpg', 1, 1), file('b.jpg', 1, 1)], new Set());
    expect(pending).toHaveLength(2);
    expect(skipped).toHaveLength(0);
  });
});

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { DEFAULT_PHOTO_TEXT, photoTextRect, validPhotoText } from './photoText.ts';

test('a text box centres and aligns with each edge without leaving the frame', () => {
  for (const x of [0, .5, 1]) for (const y of [0, .5, 1]) {
    const rect = photoTextRect({ ...DEFAULT_PHOTO_TEXT, x, y, width: .6, height: .4 });
    assert.equal(rect.left, x * .4); assert.equal(rect.top, y * .6);
    assert.ok(rect.left >= 0 && rect.left + rect.width <= 1);
    assert.ok(rect.top >= 0 && rect.top + rect.height <= 1);
  }
});
test('refuses nonfinite coordinates, off-frame boxes and unknown vocabulary', () => {
  for (const bad of [{ x: NaN }, { y: Infinity }, { width: .1 }, { height: .9 }, { x: 1.01 }, { mode: 'bad' }, { tail: 'bad' }])
    assert.equal(validPhotoText({ ...DEFAULT_PHOTO_TEXT, ...bad } as typeof DEFAULT_PHOTO_TEXT), false);
});

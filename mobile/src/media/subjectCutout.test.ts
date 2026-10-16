import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ApiError } from '../api/client.ts';
import { subjectCutoutFailure } from '../api/subjectCutout.ts';
import { canCutOutSlide, cutoutFileName } from './subjectCutout.ts';

test('only an owner photo slide can be cut out', () => {
  assert.equal(canCutOutSlide({ key: 'f1', kind: 'image', imagePath: '/api/files/f1/preview' }), true);
  // A video never.
  assert.equal(canCutOutSlide({ key: 'f1', kind: 'video', imagePath: '/api/files/f1/preview' }), false);
  // A shared-album slide: album-scoped URL, album item key.
  assert.equal(
    canCutOutSlide({ key: 'item-9', kind: 'image', imagePath: '/api/shared-albums/a1/items/item-9/preview' }),
    false,
  );
  // An owner path that is not this slide's own file.
  assert.equal(canCutOutSlide({ key: 'f2', kind: 'image', imagePath: '/api/files/f1/preview' }), false);
});

test('cutout files are named after the photo and stay safe file names', () => {
  assert.equal(cutoutFileName('IMG_0001.HEIC'), 'IMG_0001-cutout.png');
  assert.equal(cutoutFileName('Vacanze al mare'), 'Vacanze al mare-cutout.png');
  assert.equal(cutoutFileName('../../etc/passwd'), 'passwd-cutout.png');
  assert.equal(cutoutFileName('a/b\\c.jpg'), 'c-cutout.png');
  assert.equal(cutoutFileName('foto:1?.jpg'), 'foto_1_-cutout.png');
  assert.equal(cutoutFileName('.jpg'), 'photo-cutout.png');
});

test('server answers map to what the sheet says', () => {
  assert.equal(subjectCutoutFailure(new ApiError(404, 'x')), 'notFound');
  assert.equal(subjectCutoutFailure(new ApiError(422, 'x', { code: 'no_subject' })), 'noSubject');
  assert.equal(subjectCutoutFailure(new ApiError(503, 'x', { code: 'busy' })), 'busy');
  assert.equal(subjectCutoutFailure(new ApiError(503, 'x', { code: 'unavailable' })), 'unavailable');
  assert.equal(subjectCutoutFailure(new ApiError(500, 'x')), 'failed');
  assert.equal(subjectCutoutFailure(new Error('network')), 'failed');
});

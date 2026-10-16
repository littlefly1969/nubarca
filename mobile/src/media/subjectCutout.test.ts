import assert from 'node:assert/strict';
import { test } from 'node:test';
import { ApiError } from '../api/client.ts';
import { parseSubjectCutoutRegion, subjectCutoutFailure, subjectPointOnPhoto } from '../api/subjectCutout.ts';
import { canCutOutSlide, containFraction, cutoutFileName, pngSize } from './subjectCutout.ts';

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

test('a tap on a contained image maps to the image, never the bands around it', () => {
  // A 200x100 image shown in a 300x300 view: 300x150, with 75px bands above and below.
  const view = { width: 300, height: 300 };
  const image = { width: 200, height: 100 };
  assert.deepEqual(containFraction(150, 150, view, image), { fx: 0.5, fy: 0.5 });
  assert.deepEqual(containFraction(0, 75, view, image), { fx: 0, fy: 0 });
  assert.equal(containFraction(150, 40, view, image), null);
  assert.equal(containFraction(150, 150, { width: 0, height: 0 }, image), null);
});

test('a tap on the cutout becomes a point on the photo', () => {
  const region = parseSubjectCutoutRegion('0.5,0,0.5,1');
  assert.deepEqual(region, { x: 0.5, y: 0, width: 0.5, height: 1 });
  assert.deepEqual(subjectPointOnPhoto(region!, 0.5, 0.5), { x: 0.75, y: 0.5 });
  assert.equal(parseSubjectCutoutRegion(null), null);
  assert.equal(parseSubjectCutoutRegion('0,0,1'), null);
  assert.equal(parseSubjectCutoutRegion('0,0,1.5,1'), null);
});

test('the PNG size is read from its header', () => {
  const bytes = new Uint8Array(24);
  bytes.set([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
  new DataView(bytes.buffer).setUint32(16, 640);
  new DataView(bytes.buffer).setUint32(20, 480);
  assert.deepEqual(pngSize(bytes.buffer), { width: 640, height: 480 });
  assert.equal(pngSize(new Uint8Array(10).buffer), null);
  assert.equal(pngSize(new Uint8Array(24).buffer), null);
});

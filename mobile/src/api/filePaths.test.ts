// File-path builder tests: these strings are SERVER CONTRACTS shared with the
// web client and TV; a typo here is a silent 404 on every grid tile.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  fileThumbnailPath,
  filePreviewPath,
  filePosterPath,
  fileVideoPath,
} from './filePaths.ts';

const ID = '0123abcd-4567-89ef';

test('thumbnail path carries the small size parameter', () => {
  assert.equal(fileThumbnailPath(ID), `/api/files/${ID}/thumbnail?size=small`);
});

test('preview path is the medium derivative', () => {
  assert.equal(filePreviewPath(ID), `/api/files/${ID}/preview`);
});

test('poster path serves the video poster', () => {
  assert.equal(filePosterPath(ID), `/api/files/${ID}/poster`);
});

test('video path is the Range-enabled playback endpoint', () => {
  assert.equal(fileVideoPath(ID), `/api/files/${ID}/video`);
});

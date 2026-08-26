// Download-name helper tests: the saved ORIGINAL must keep what the server
// declared (filename / MIME) — never a kind-guessed extension.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  buildDownloadName,
  parseAttachmentFilename,
  pickHeader,
} from './downloadName.ts';

test('pickHeader is case-insensitive and tolerates arrays', () => {
  const headers = {
    'Content-Type': 'image/heic',
    'content-disposition': ['attachment; filename="a.jpg"'],
    'x-empty': undefined,
  } as Record<string, string | string[] | undefined>;
  assert.equal(pickHeader(headers, 'content-type'), 'image/heic');
  assert.equal(pickHeader(headers, 'CONTENT-DISPOSITION'), 'attachment; filename="a.jpg"');
  assert.equal(pickHeader(headers, 'x-empty'), null);
  assert.equal(pickHeader(headers, 'missing'), null);
});

test('parses RFC 5987 extended filenames with percent-encoding', () => {
  // na%C3%AFve.jpg → "naïve.jpg"
  assert.equal(
    parseAttachmentFilename("attachment; filename*=UTF-8''na%C3%AFve.jpg"),
    'naïve.jpg',
  );
});

test('parses plain quoted filenames', () => {
  assert.equal(
    parseAttachmentFilename('attachment; filename="Vacanze 2026.heic"'),
    'Vacanze 2026.heic',
  );
});

test('absent or malformed disposition yields null', () => {
  assert.equal(parseAttachmentFilename(null), null);
  assert.equal(parseAttachmentFilename('inline'), null);
});

test('path components in a server filename are stripped', () => {
  assert.equal(
    parseAttachmentFilename('attachment; filename="../../etc/passwd"'),
    'passwd',
  );
});

test('buildDownloadName keeps a complete server name as-is', () => {
  assert.equal(
    buildDownloadName({
      disposition: 'attachment; filename="Natale.MOV"',
      mimeType: 'video/quicktime',
      kindFallbackExtension: 'mp4',
    }),
    'Natale.MOV',
  );
});

test('buildDownloadName completes an extension-less server name from MIME', () => {
  assert.equal(
    buildDownloadName({
      disposition: 'attachment; filename="scansione"',
      mimeType: 'image/heic',
      kindFallbackExtension: 'jpg',
    }),
    'scansione.heic',
  );
});

test('buildDownloadName falls back to MIME, then to the media kind', () => {
  const fromMime = buildDownloadName({
    disposition: 'attachment',
    mimeType: 'application/png',
    kindFallbackExtension: 'mp4',
  });
  assert.equal(fromMime, 'nubarca-original.png');

  const fromKind = buildDownloadName({
    disposition: null,
    mimeType: 'application/octet-stream',
    kindFallbackExtension: 'mov',
  });
  assert.equal(fromKind, 'nubarca-original.mov');
});

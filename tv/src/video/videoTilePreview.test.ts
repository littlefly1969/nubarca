import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { videoTilePreview } from './videoTilePreview.ts';

const POSTER = '/api/tv/personal/media/1/poster';
const STILL = '/api/tv/personal/media/1/thumbnail';
const STRIP = '/api/tv/personal/media/1/video-preview-strip';

test('a poster is used when it exists', () => {
  assert.deepEqual(
    videoTilePreview({ posterUrl: POSTER, stillFallbackUrl: STILL }),
    { kind: 'poster', path: POSTER, fallbackPath: STILL },
  );
});

test('a missing poster falls back to the item own still', () => {
  assert.deepEqual(
    videoTilePreview({ posterUrl: null, stillFallbackUrl: STILL }),
    { kind: 'poster', path: STILL, fallbackPath: null },
  );
});

test('no poster and no still is an EXPLICIT placeholder, never a blank tile', () => {
  // The product requirement: a failed preview must read as "video, no preview",
  // never as an empty focusable rectangle indistinguishable from a broken app.
  assert.deepEqual(videoTilePreview({ posterUrl: null }), { kind: 'placeholder' });
  assert.deepEqual(videoTilePreview({ posterUrl: '', stillFallbackUrl: '  ' }),
    { kind: 'placeholder' });
});

test('the six-cell preview strip is never used as a tile image', () => {
  // It is a 2880x270 sprite of six frames: at `contain` a hairline band, at
  // `cover` an arbitrary crop. Passing it through must change nothing.
  assert.deepEqual(
    videoTilePreview({ posterUrl: null, previewStripUrl: STRIP }),
    { kind: 'placeholder' },
  );
  assert.deepEqual(
    videoTilePreview({ posterUrl: POSTER, previewStripUrl: STRIP }),
    { kind: 'poster', path: POSTER, fallbackPath: null },
  );
});

test('a fallback identical to the primary is dropped', () => {
  // Retrying the exact URL the loader just memoized as failed is not a fallback.
  assert.deepEqual(
    videoTilePreview({ posterUrl: POSTER, stillFallbackUrl: POSTER }),
    { kind: 'poster', path: POSTER, fallbackPath: null },
  );
});

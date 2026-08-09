import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { previewPriority, videoTilePreview } from './videoTilePreview.ts';

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

test('preview warming is bounded to the neighbourhood of the focused tile', () => {
  const columns = 5;
  const focused = 20; // row 4, column 0
  assert.equal(previewPriority(20, focused, columns), 'high');
  // Same row and the rows immediately above/below: visible.
  assert.equal(previewPriority(22, focused, columns), 'normal');
  assert.equal(previewPriority(25, focused, columns), 'normal');
  assert.equal(previewPriority(15, focused, columns), 'normal');
  // One more row out: warm at low priority so a row change is not a wall of
  // placeholders.
  assert.equal(previewPriority(29, focused, columns), 'low');
  assert.equal(previewPriority(11, focused, columns), 'low');
  // Far away: not warmed at all. Warming a whole library is what made the grid
  // thrash the bounded download pool.
  assert.equal(previewPriority(60, focused, columns), 'none');
  assert.equal(previewPriority(0, focused, columns), 'none');
});

test('before anything is focused only the first rows are warmed', () => {
  assert.equal(previewPriority(0, -1, 5), 'normal');
  assert.equal(previewPriority(9, -1, 5), 'normal');
  assert.equal(previewPriority(10, -1, 5), 'none');
});

import assert from 'node:assert/strict';
import test from 'node:test';
import {
  getTvMediaAspectRatio,
  normalizeTvMediaAspectRatio,
  MIN_ASPECT_RATIO,
  MAX_ASPECT_RATIO,
  PHOTO_FALLBACK_ASPECT_RATIO,
  VIDEO_FALLBACK_ASPECT_RATIO,
} from './mediaAspectRatio.ts';

function near(actual: number, expected: number, eps = 1e-9): void {
  assert.ok(Math.abs(actual - expected) < eps, `${actual} !~= ${expected}`);
}

test('normalizeTvMediaAspectRatio returns the real ratio for valid dimensions', () => {
  near(normalizeTvMediaAspectRatio(1920, 1080, 1), 16 / 9);
  near(normalizeTvMediaAspectRatio(1080, 1920, 1), 9 / 16);
  assert.equal(normalizeTvMediaAspectRatio(1000, 1000, 1), 1);
});

test('normalizeTvMediaAspectRatio falls back on missing/zero/negative/NaN', () => {
  for (const [w, h] of [[null, 1080], [1920, null], [0, 1080], [1920, 0], [-1, 2], [Number.NaN, 2], [2, Number.POSITIVE_INFINITY]] as const) {
    assert.equal(normalizeTvMediaAspectRatio(w, h, 2), 2);
  }
});

test('normalizeTvMediaAspectRatio clamps extremes into [0.35, 3.5]', () => {
  assert.equal(normalizeTvMediaAspectRatio(100, 5000, 1), MIN_ASPECT_RATIO);
  assert.equal(normalizeTvMediaAspectRatio(5000, 100, 1), MAX_ASPECT_RATIO);
  near(normalizeTvMediaAspectRatio(2560, 1080, 1), 2560 / 1080);
});

test('getTvMediaAspectRatio uses real ratios for photo and video', () => {
  near(getTvMediaAspectRatio({ mediaType: 'image', width: 4000, height: 3000 }), 4 / 3);
  near(getTvMediaAspectRatio({ mediaType: 'image', width: 3000, height: 4000 }), 3 / 4);
  near(getTvMediaAspectRatio({ mediaType: 'video', width: 1920, height: 1080 }), 16 / 9);
  // A vertical video is NOT forced to 16:9.
  const vertical = getTvMediaAspectRatio({ mediaType: 'video', width: 1080, height: 1920 });
  near(vertical, 9 / 16);
  assert.notEqual(vertical, VIDEO_FALLBACK_ASPECT_RATIO);
});

test('getTvMediaAspectRatio applies per-kind fallbacks for missing dims', () => {
  assert.equal(getTvMediaAspectRatio({ mediaType: 'image', width: null, height: null }), PHOTO_FALLBACK_ASPECT_RATIO);
  assert.equal(getTvMediaAspectRatio({ mediaType: 'video', width: null, height: null }), VIDEO_FALLBACK_ASPECT_RATIO);
  near(VIDEO_FALLBACK_ASPECT_RATIO, 16 / 9);
});

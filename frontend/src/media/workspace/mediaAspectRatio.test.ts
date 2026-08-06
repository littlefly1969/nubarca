import { describe, expect, it } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import {
  getMediaAspectRatio,
  normalizeAspectRatio,
  MAX_ASPECT_RATIO,
  MIN_ASPECT_RATIO,
  PHOTO_FALLBACK_ASPECT_RATIO,
  VIDEO_FALLBACK_ASPECT_RATIO,
} from './mediaAspectRatio';

function photo(over: Partial<MediaItem> = {}): MediaItem {
  return {
    id: 'p', kind: 'image', name: 'p.jpg', title: null, displayName: 'p.jpg',
    mimeType: 'image/jpeg', sizeBytes: 1, width: null, height: null,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: null, takenAt: null,
    favorite: false, rating: null, thumbnailUrl: '/t', occurrenceCount: 1,
    hasDuplicates: false, hasGps: null, ...over,
  } as MediaItem;
}

function video(over: Partial<MediaItem> = {}): MediaItem {
  return {
    id: 'v', kind: 'video', name: 'v.mp4', title: null, displayName: 'v.mp4',
    mimeType: 'video/mp4', sizeBytes: 1, width: null, height: null,
    createdAt: '2026-01-01T00:00:00Z', updatedAt: null, takenAt: null,
    favorite: false, rating: null, thumbnailUrl: '/t', occurrenceCount: 1,
    hasDuplicates: false, posterUrl: '/p', durationSeconds: 1, videoCodec: 'h264',
    hasAudio: true, posterSource: 'ffmpeg', previewStripUrl: null, ...over,
  } as MediaItem;
}

describe('normalizeAspectRatio', () => {
  it('returns the real ratio for valid dimensions', () => {
    expect(normalizeAspectRatio(1920, 1080, 1)).toBeCloseTo(16 / 9);
    expect(normalizeAspectRatio(1080, 1920, 1)).toBeCloseTo(9 / 16);
    expect(normalizeAspectRatio(1000, 1000, 1)).toBe(1);
  });

  it('falls back when a dimension is missing, zero, negative or non-finite', () => {
    expect(normalizeAspectRatio(null, 1080, 2)).toBe(2);
    expect(normalizeAspectRatio(1920, null, 2)).toBe(2);
    expect(normalizeAspectRatio(undefined, undefined, 2)).toBe(2);
    expect(normalizeAspectRatio(0, 1080, 2)).toBe(2);
    expect(normalizeAspectRatio(1920, 0, 2)).toBe(2);
    expect(normalizeAspectRatio(-1920, 1080, 2)).toBe(2);
    expect(normalizeAspectRatio(1920, -1080, 2)).toBe(2);
    expect(normalizeAspectRatio(Number.NaN, 1080, 2)).toBe(2);
    expect(normalizeAspectRatio(1920, Number.POSITIVE_INFINITY, 2)).toBe(2);
  });

  it('clamps pathologically narrow and wide ratios into the prudent band', () => {
    // 100×5000 → 0.02, clamped up to the min.
    expect(normalizeAspectRatio(100, 5000, 1)).toBe(MIN_ASPECT_RATIO);
    // 5000×100 → 50, clamped down to the max.
    expect(normalizeAspectRatio(5000, 100, 1)).toBe(MAX_ASPECT_RATIO);
  });

  it('leaves a realistic panorama untouched (within the band)', () => {
    expect(normalizeAspectRatio(2560, 1080, 1)).toBeCloseTo(2560 / 1080);
  });
});

describe('getMediaAspectRatio', () => {
  it('uses the real ratio for a horizontal photo', () => {
    expect(getMediaAspectRatio(photo({ width: 4000, height: 3000 }))).toBeCloseTo(4 / 3);
  });

  it('uses the real ratio for a vertical photo', () => {
    expect(getMediaAspectRatio(photo({ width: 3000, height: 4000 }))).toBeCloseTo(3 / 4);
  });

  it('uses the real ratio for a square photo', () => {
    expect(getMediaAspectRatio(photo({ width: 2000, height: 2000 }))).toBe(1);
  });

  it('uses the real ratio for a horizontal video', () => {
    expect(getMediaAspectRatio(video({ width: 1920, height: 1080 }))).toBeCloseTo(16 / 9);
  });

  it('uses the real ratio for a vertical video (NOT forced to 16:9)', () => {
    const ratio = getMediaAspectRatio(video({ width: 1080, height: 1920 }));
    expect(ratio).toBeCloseTo(9 / 16);
    expect(ratio).not.toBeCloseTo(VIDEO_FALLBACK_ASPECT_RATIO);
  });

  it('uses the real ratio for a square video', () => {
    expect(getMediaAspectRatio(video({ width: 1080, height: 1080 }))).toBe(1);
  });

  it('uses the real ratio for a panoramic video', () => {
    expect(getMediaAspectRatio(video({ width: 2560, height: 1080 }))).toBeCloseTo(2560 / 1080);
  });

  it('falls back to 1:1 for a photo with missing dimensions', () => {
    expect(getMediaAspectRatio(photo({ width: null, height: null }))).toBe(PHOTO_FALLBACK_ASPECT_RATIO);
  });

  it('falls back to 16:9 for a video with missing dimensions', () => {
    expect(getMediaAspectRatio(video({ width: null, height: null }))).toBe(VIDEO_FALLBACK_ASPECT_RATIO);
    expect(VIDEO_FALLBACK_ASPECT_RATIO).toBeCloseTo(16 / 9);
  });

  it('falls back on invalid (zero) dimensions per kind', () => {
    expect(getMediaAspectRatio(photo({ width: 0, height: 0 }))).toBe(PHOTO_FALLBACK_ASPECT_RATIO);
    expect(getMediaAspectRatio(video({ width: 0, height: 0 }))).toBe(VIDEO_FALLBACK_ASPECT_RATIO);
  });
});

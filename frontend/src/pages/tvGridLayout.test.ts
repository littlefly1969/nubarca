import { describe, expect, it } from 'vitest';
import type { TvAlbumItem } from '@nubarca/api-client';
import { buildTvRows, getTvMediaAspectRatio } from './tvGridLayout';

function item(over: Partial<TvAlbumItem>): TvAlbumItem {
  return {
    id: 'x', name: 'x', mediaType: 'image', width: null, height: null,
    thumbnailUrl: '/t', previewUrl: '/p', posterUrl: null, videoUrl: null,
    previewStripUrl: null, ...over,
  };
}

describe('getTvMediaAspectRatio', () => {
  it('uses the real ratio for photos and videos', () => {
    expect(getTvMediaAspectRatio(item({ mediaType: 'image', width: 4000, height: 3000 }))).toBeCloseTo(4 / 3);
    expect(getTvMediaAspectRatio(item({ mediaType: 'video', width: 1920, height: 1080 }))).toBeCloseTo(16 / 9);
  });

  it('does NOT force a vertical video to 16:9', () => {
    expect(getTvMediaAspectRatio(item({ mediaType: 'video', width: 1080, height: 1920 }))).toBeCloseTo(9 / 16);
  });

  it('falls back to 1:1 for photos and 16:9 for videos without dimensions', () => {
    expect(getTvMediaAspectRatio(item({ mediaType: 'image', width: null, height: null }))).toBe(1);
    expect(getTvMediaAspectRatio(item({ mediaType: 'video', width: null, height: null }))).toBeCloseTo(16 / 9);
  });

  it('clamps extreme ratios', () => {
    expect(getTvMediaAspectRatio(item({ mediaType: 'image', width: 6000, height: 100 }))).toBe(3.5);
    expect(getTvMediaAspectRatio(item({ mediaType: 'image', width: 100, height: 6000 }))).toBe(0.35);
  });
});

describe('buildTvRows', () => {
  it('gives a vertical video a taller-than-wide tile', () => {
    const rows = buildTvRows([item({ id: 'v', mediaType: 'video', width: 1080, height: 1920 })], 1600);
    const tile = rows[0].items[0];
    expect(tile.height).toBeGreaterThan(tile.width);
  });

  it('justifies full rows to the container width and does not stretch the last row', () => {
    const items = Array.from({ length: 9 }, (_v, i) => item({ id: `p${i}`, width: 1600, height: 1000 }));
    const rows = buildTvRows(items, 1600);
    expect(rows.length).toBeGreaterThan(1);
    rows.forEach((row) => {
      if (row.isLastRow) return;
      // Full rows fill the width (± rounding is folded into the last tile).
      expect(row.width).toBe(1600);
    });
  });

  it('preserves source order through originalIndex', () => {
    const items = Array.from({ length: 6 }, (_v, i) => item({ id: `i${i}`, width: 1000, height: 1000 }));
    const flat = buildTvRows(items, 1600).flatMap((r) => r.items);
    flat.forEach((tile, i) => expect(tile.id).toBe(`i${i}`));
  });
});

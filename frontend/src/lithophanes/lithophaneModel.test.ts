import { describe, expect, it } from 'vitest';
import { activeLithophane, DEFAULT_LITHOPHANE_SETTINGS as defaults, latitudeRow, lithophaneTriangleCount, suggestedLithophaneResolution, validLithophaneSettings } from './lithophaneModel';

describe('lithophane form feedback', () => {
  it('accepts defaults and rejects non-finite, impossible and over-detailed models', () => {
    expect(validLithophaneSettings(defaults)).toBe(true);
    for (const change of [{ diameterMm: NaN }, { gamma: Infinity }, { openingMm: 79 },
      { maxThicknessMm: 0.4 }, { resolutionMm: 0 }, { diameterMm: 200, resolutionMm: 0.15 }]) {
      expect(validLithophaneSettings({ ...defaults, ...change })).toBe(false);
    }
  });
  it('counts triangles exactly like the server near the 2 million budget', () => {
    // Same table as LithophaneGeometryTests.Triangle_Count_Matches_The_Browser_Form.
    // [diameter, opening, minimum wall, step, triangles]
    const table: [number, number, number, number, number][] = [
    [80, 20, 0.8, 0.3, 1266720],
    [95, 20, 0.8, 0.3, 1804752],
    [96, 20, 0.8, 0.3, 1850688],
    [100, 30, 1.2, 0.3, 1932512],
    [120, 40, 0.8, 0.35, 2052000],
    [120, 40, 0.8, 0.4, 1567040],
    [150, 20, 0.8, 0.45, 2058272],
    [150, 20, 0.8, 0.5, 1668992],
    [200, 130, 2, 0.6, 1676800],
    [200, 4, 0.4, 0.65, 1819840],
    [40, 4, 0.4, 0.15, 1310400],
    [60, 39, 0.6, 0.2, 1359360],
    ];
    for (const [diameterMm, openingMm, minThicknessMm, resolutionMm, triangles] of table) {
      const s = { ...defaults, diameterMm, openingMm, minThicknessMm, resolutionMm, maxThicknessMm: minThicknessMm + 2 };
      expect(lithophaneTriangleCount(s)).toBe(triangles);
      expect(validLithophaneSettings(s)).toBe(triangles <= 2_000_000);
    }
  });
  it('suggests the finest step that fits when only the detail is too high', () => {
    expect(suggestedLithophaneResolution(defaults)).toBeNull();
    expect(suggestedLithophaneResolution({ ...defaults, maxThicknessMm: 0.4 })).toBeNull();
    for (const diameterMm of [100, 120, 160, 200]) {
      const big = { ...defaults, diameterMm };
      expect(validLithophaneSettings(big)).toBe(false);
      const step = suggestedLithophaneResolution(big)!;
      expect(validLithophaneSettings({ ...big, resolutionMm: step })).toBe(true);
      expect(validLithophaneSettings({ ...big, resolutionMm: Math.round((step - 0.05) * 100) / 100 })).toBe(false);
    }
  });
  it('distinguishes active runs from terminal results', () => {
    expect(['queued', 'running', 'cancelling'].every(activeLithophane)).toBe(true);
    expect(['ready', 'failed', 'cancelled'].some(activeLithophane)).toBe(false);
  });
  it('maps pole, exact equator and aperture to the persisted pixel grid', () => {
    expect(latitudeRow(0, 20, 37, 2.9)).toBe(0);
    expect(latitudeRow(Math.PI / 2, 20, 37, 2.9)).toBe(20);
    expect(latitudeRow(2.9, 20, 37, 2.9)).toBe(37);
  });
});

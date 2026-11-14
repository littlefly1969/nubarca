import { describe, expect, it } from 'vitest';
import { activeLithophane, DEFAULT_LITHOPHANE_SETTINGS as defaults, latitudeRow, suggestedLithophaneResolution, validLithophaneSettings } from './lithophaneModel';

describe('lithophane form feedback', () => {
  it('accepts defaults and rejects non-finite, impossible and over-detailed models', () => {
    expect(validLithophaneSettings(defaults)).toBe(true);
    for (const change of [{ diameterMm: NaN }, { gamma: Infinity }, { openingMm: 79 },
      { maxThicknessMm: 0.4 }, { resolutionMm: 0 }, { diameterMm: 200, resolutionMm: 0.15 }]) {
      expect(validLithophaneSettings({ ...defaults, ...change })).toBe(false);
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

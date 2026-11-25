import { describe, expect, it } from 'vitest';
import { activeLithophane, DEFAULT_LITHOPHANE_SETTINGS as defaults, latitudeRow, lithophaneRequestSettings, lithophaneTriangleCount, suggestedLithophaneResolution, validLithophaneSettings, validLithophaneText } from './lithophaneModel';

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
    // [diameter, opening, minimum wall, step, legacy triangles, collar triangles]
    const table: [number, number, number, number, number, number][] = [
    [80, 20, 0.8, 0.3, 1266720, 1253280],
    [95, 20, 0.8, 0.3, 1804752, 1800768],
    [96, 20, 0.8, 0.3, 1850688, 1842624],
    [100, 30, 1.2, 0.3, 1932512, 1915744],
    [120, 40, 0.8, 0.35, 2052000, 2017440],
    [120, 40, 0.8, 0.4, 1567040, 1544384],
    [150, 20, 0.8, 0.45, 2058272, 2062464],
    [150, 20, 0.8, 0.5, 1668992, 1672768],
    [200, 130, 2, 0.6, 1676800, 1660032],
    [200, 4, 0.4, 0.65, 1819840, 1823712],
    [40, 4, 0.4, 0.15, 1310400, 1313760],
    [60, 39, 0.6, 0.2, 1359360, 1246080],
    ];
    for (const [diameterMm, openingMm, minThicknessMm, resolutionMm, triangles, collarTriangles] of table) {
      const s = { ...defaults, externalRelief: false, baseCollar: false, diameterMm, openingMm, minThicknessMm, resolutionMm, maxThicknessMm: minThicknessMm + 2 };
      expect(lithophaneTriangleCount(s)).toBe(triangles);
      expect(validLithophaneSettings(s)).toBe(triangles <= 2_000_000);
      expect(lithophaneTriangleCount({ ...s, baseCollar: true })).toBe(collarTriangles);
      expect(validLithophaneSettings({ ...s, baseCollar: true })).toBe(collarTriangles <= 2_000_000);
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
  it('matches the external-relief and sacrificial-anchor budgets on the server', () => {
    const cases = [
      [80, 30, 0.4, 0.3, false, false, 1179360],
      [80, 30, 0.4, 0.3, true, true, 1221360],
      [80, 40, 0.8, 0.3, true, true, 1164240],
      [40, 16, 0.8, 1, true, true, 28928],
      [200, 130, 0.8, 0.6, true, false, 1693568],
    ] as const;
    for (const [diameterMm, openingMm, minThicknessMm, resolutionMm, hanging, printAnchor, expected] of cases) {
      const settings = { ...defaults, diameterMm, openingMm, minThicknessMm, resolutionMm, hanging, printAnchor };
      expect(lithophaneTriangleCount(settings)).toBe(expected);
      expect(validLithophaneSettings(settings)).toBe(true);
    }
  });
  it('distinguishes active runs from terminal results', () => {
    expect(['queued', 'running', 'cancelling'].every(activeLithophane)).toBe(true);
    expect(['ready', 'failed', 'cancelled'].some(activeLithophane)).toBe(false);
  });
  it('budgets the internal joint and both brim options and rejects incompatible settings', () => {
    const s = { ...defaults, hanging: true, openingMm: 30, printAnchor: false, internalFit: true };
    // One extra ring boundary per profile segment; the cap is a separate STL.
    const plain = lithophaneTriangleCount({ ...s, internalFit: false });
    const columns = 4 * Math.ceil(Math.PI * s.diameterMm / s.resolutionMm / 4);
    expect(lithophaneTriangleCount(s)).toBe(plain + 6 * columns);
    expect(lithophaneTriangleCount({ ...s, printBrim: true })).toBe(plain + 10 * columns);
    expect(validLithophaneSettings({ ...s, printBrim: true })).toBe(true);
    expect(validLithophaneSettings({ ...s, hanging: false })).toBe(false);
    expect(validLithophaneSettings({ ...s, printAnchor: true })).toBe(false);
    expect(validLithophaneSettings({ ...s, internalFit: false, printBrim: true })).toBe(false);
    // Same independent fixtures as Internal_Joint_Triangle_Budget_Matches_The_Browser.
    const cases = [[80, 30, 0.3, false, 1218000], [80, 30, 0.3, true, 1221360],
      [40, 16, 1, false, 28416], [40, 16, 1, true, 28928],
      [200, 130, 0.6, false, 1699856], [200, 130, 0.6, true, 1704048]] as const;
    for (const [diameterMm, openingMm, resolutionMm, printBrim, expected] of cases)
      expect(lithophaneTriangleCount({ ...s, diameterMm, openingMm, resolutionMm, printBrim })).toBe(expected);
  });
  it('maps pole, exact equator and aperture to the persisted pixel grid', () => {
    expect(latitudeRow(0, 20, 37, 2.9)).toBe(0);
    expect(latitudeRow(Math.PI / 2, 20, 37, 2.9)).toBe(20);
    expect(latitudeRow(2.9, 20, 37, 2.9)).toBe(37);
  });
});

describe('lithophane words', () => {
  it('accepts one or two lines and refuses what the server refuses', () => {
    expect(validLithophaneText('Ciao')).toBe(true);
    expect(validLithophaneText('Buon\nNatale')).toBe(true);
    expect(validLithophaneText('a'.repeat(60))).toBe(true);
    for (const bad of ['a'.repeat(61), 'uno\ndue\ntre', 'tab\tqui', 'festa \u{1F389}']) expect(validLithophaneText(bad)).toBe(false);
    expect(validLithophaneSettings({ ...defaults, text: 'Ok', textHeightMm: 2 })).toBe(false);
    expect(validLithophaneSettings({ ...defaults, text: 'Ok', textHeightMm: 30 })).toBe(true);
  });
  it('sends the default face and position as absent, and nothing without words', () => {
    expect(lithophaneRequestSettings({ ...defaults, text: '  ', textFont: 'script' })).toEqual((({ textLatitudeDegrees: _lat, textLongitudeDegrees: _lon, ...shape }) => shape)(defaults));
    expect(lithophaneRequestSettings({ ...defaults, text: 'Ciao', textFont: 'nubarca', textPosition: 'below' }))
      .toEqual({ ...defaults, text: 'Ciao', textHeightMm: 8 });
  });
});

it('accepts a 0.4 mm suspended wall with an internal joint and bounds soft relief', () => {
  const thin = { ...defaults, hanging: true, internalFit: true, openingMm: 30, minThicknessMm: 0.4, backgroundThicknessMm: 0.4 };
  expect(validLithophaneSettings(thin)).toBe(true);
  expect(validLithophaneSettings({ ...thin, internalFit: false })).toBe(false);
  expect(validLithophaneSettings({ ...thin, backgroundTextureHeightMm: 0.15, backgroundTextureGrainMm: 1.5 })).toBe(true);
  expect(validLithophaneSettings({ ...thin, backgroundTextureHeightMm: 0.5 })).toBe(false);
});

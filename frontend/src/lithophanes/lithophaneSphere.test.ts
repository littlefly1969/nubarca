import { describe, expect, it } from 'vitest';
import { spherePixels } from './lithophaneSphere';

const grid = { rows: 16, topRows: 8, bottomAngle: 2.8 };
const centre = (pixels: Uint8ClampedArray) => pixels[(16 * 32 + 16) * 4];
function map(value: number) {
  const data = new Uint8ClampedArray(32 * 17 * 4).fill(value);
  return { width: 32, height: 17, data };
}

describe('spherical lithophane preview', () => {
  it('reveals tonal contrast under light and has no photo on the smooth unlit exterior', () => {
    const white = map(255); const black = map(0);
    expect(centre(spherePixels(white, grid, 0, true, 32))).toBeGreaterThan(240);
    expect(centre(spherePixels(black, grid, 0, true, 32))).toBeLessThan(40);
    expect(spherePixels(white, grid, 0, false, 32)).toEqual(spherePixels(black, grid, 0, false, 32));
  });
  it('wraps rotation at the seam and puts the front of the photograph at the front of the sphere', () => {
    const photo = map(255);
    for (let row = 0; row < 17; row++) for (let col = 12; col <= 20; col++)
      photo.data[(row * 32 + col) * 4] = 0;
    const front = spherePixels(photo, grid, 0, true, 32);
    const back = spherePixels(photo, grid, 180, true, 32);
    expect(centre(front)).toBeLessThan(40);
    expect(centre(back)).toBeGreaterThan(240);
    expect(back).toEqual(spherePixels(photo, grid, -180, true, 32));
  });
  it('keeps the exterior and the removed bottom cap transparent', () => {
    const frame = spherePixels(map(255), grid, 0, true, 32);
    expect(frame[3]).toBe(0);
    expect(frame[(31 * 32 + 16) * 4 + 3]).toBe(0);
    expect(frame[(16 * 32 + 16) * 4 + 3]).toBe(255);
  });
  it('turns a hanging map upright for use and leaves it inverted in the printing view', () => {
    const photo = map(255);
    for (let row = 0; row < 8; row++) for (let col = 0; col < 32; col++) photo.data[(row * 32 + col) * 4] = 0;
    const hanging = { ...grid, hanging: true, crownRadiusRatio: 0.125 };
    const use = spherePixels(photo, hanging, 0, true, 64);
    const print = spherePixels(photo, hanging, 0, true, 64, true);
    const top = (20 * 64 + 32) * 4;
    const bottom = (44 * 64 + 32) * 4;
    expect(use[top]).toBeGreaterThan(use[bottom] + 100);
    expect(print[bottom]).toBeGreaterThan(print[top] + 100);
  });
  it('shows external relief under unlit shading, including its larger silhouette', () => {
    const photo = map(255);
    for (let row = 2; row < 15; row++) for (let col = 12; col < 20; col++) photo.data[(row * 32 + col) * 4] = 0;
    const relief = { ...grid, externalRelief: true, innerRadiusRatio: 0.98, minWallRatio: 0.02, maxWallRatio: 0.08 };
    expect(spherePixels(photo, relief, 0, false, 64)).not.toEqual(spherePixels(map(255), relief, 0, false, 64));
  });
  it('shows a straight collar below the sphere with a constant width and its own wall transmission', () => {
    const size = 200;
    const base = { ...grid, bottomAngle: Math.acos(-0.8), baseHeightRatio: 0.15, baseRadiusRatio: 0.6, baseTransmission: 0 };
    const frame = spherePixels(map(255), base, 0, true, size);
    const legacy = spherePixels(map(255), { ...base, baseHeightRatio: 0 }, 0, true, size);
    const opaqueXs = (y: number) => Array.from({ length: size }, (_, x) => x)
      .filter((x) => frame[(y * size + x) * 4 + 3] === 255);
    // Both rows lie below the sphere's cut plane and inside the vertical base.
    expect(opaqueXs(176)).toEqual(opaqueXs(184));
    expect(opaqueXs(176).length).toBeGreaterThan(100);
    const centrePixel = (180 * size + size / 2) * 4;
    expect(frame[centrePixel + 3]).toBe(255);
    expect(frame[centrePixel]).toBeLessThan(40);
    expect(legacy[centrePixel + 3]).toBe(0);
    expect(frame[(190 * size + size / 2) * 4 + 3]).toBe(0);
  });
  it('keeps the internal joint out of the exterior silhouette and confines its illustrative shadow to the opening', () => {
    const size = 400;
    const plain = { ...grid, bottomAngle: Math.acos(-0.9), hanging: true, crownRadiusRatio: 0.125 };
    const joint = { ...plain, fitDepthRatio: 0.05, fitRadiusRatio: 0.375 };
    const white = map(255);
    expect(spherePixels(white, joint, 0, false, size)).toEqual(spherePixels(white, plain, 0, false, size));
    const lit = spherePixels(white, joint, 0, true, size);
    const before = spherePixels(white, plain, 0, true, size);
    let changed = 0;
    for (let i = 0; i < lit.length; i += 4) {
      expect(lit[i + 3]).toBe(before[i + 3]);
      if (lit[i] !== before[i]) { changed++; expect(lit[i]).toBeLessThan(before[i]); }
    }
    expect(changed).toBeGreaterThan(0);
    const centrePixel = (size / 2 * size + size / 2) * 4;
    expect(lit[centrePixel]).toBe(before[centrePixel]);
  });
});

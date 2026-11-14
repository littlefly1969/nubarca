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
});

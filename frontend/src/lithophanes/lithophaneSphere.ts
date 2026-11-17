import type { LithophanePreview } from '@nubarca/api-client';
import { latitudeRow } from './lithophaneModel';

type PixelMap = Pick<ImageData, 'width' | 'height' | 'data'>;
type Grid = Pick<LithophanePreview, 'rows' | 'topRows' | 'bottomAngle' | 'baseHeightRatio' | 'baseRadiusRatio' | 'baseTransmission'>;

// Orthographic preview of the actual persisted luminance map. Independent of
// canvas so projection, seam wrapping and lighting are numerically testable.
export function spherePixels(map: PixelMap, grid: Grid, rotation: number, lit: boolean, size = 360): Uint8ClampedArray {
  const frame = new Uint8ClampedArray(size * size * 4);
  const radius = size * 0.46;
  const yaw = rotation * Math.PI / 180;
  const bottomZ = Math.cos(grid.bottomAngle);
  for (let y = 0; y < size; y++) {
    const z = (size / 2 - y - 0.5) / radius;
    const onBase = z < bottomZ && z >= bottomZ - (grid.baseHeightRatio ?? 0);
    if (!onBase && (Math.abs(z) > 1 || z < bottomZ)) continue;
    const theta = onBase ? grid.bottomAngle : Math.acos(z);
    // Latitude depends on the screen row only: calculate it once per row.
    const row = Math.min(map.height - 1, Math.max(0, Math.round(
      latitudeRow(theta, grid.topRows, grid.rows, grid.bottomAngle))));
    for (let x = 0; x < size; x++) {
      const nx = (x + 0.5 - size / 2) / radius;
      const square = onBase ? (grid.baseRadiusRatio ?? 0) ** 2 - nx * nx : 1 - nx * nx - z * z;
      if (square < 0) continue;
      const depth = Math.sqrt(square);
      const phi = Math.atan2(nx, depth) + yaw;
      const u = ((phi / (2 * Math.PI) + 0.5) % 1 + 1) % 1;
      const col = Math.min(map.width - 1, Math.floor(u * map.width));
      const value = onBase ? (grid.baseTransmission ?? 0) : map.data[(row * map.width + col) * 4] / 255;
      const normalDepth = onBase ? depth / (grid.baseRadiusRatio ?? 1) : depth;
      const light = lit ? (0.12 + 0.88 * value) * (0.8 + 0.2 * normalDepth)
        : Math.min(1, 0.58 + normalDepth * 0.28 + z * 0.1 - nx * 0.06);
      const i = (y * size + x) * 4;
      frame[i] = Math.round(255 * light);
      frame[i + 1] = Math.round((lit ? 239 : 248) * light);
      frame[i + 2] = Math.round((lit ? 207 : 245) * light);
      frame[i + 3] = 255;
    }
  }
  return frame;
}

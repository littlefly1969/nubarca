import type { LithophanePreview } from '@nubarca/api-client';
import { latitudeRow } from './lithophaneModel';

type PixelMap = Pick<ImageData, 'width' | 'height' | 'data'>;
type Grid = Omit<LithophanePreview, 'imageBase64' | 'columns' | 'triangleCount' | 'sizeBytes'>;

// Orthographic illustration from the SAME persisted map as the mesh. External
// relief uses wall radii and tone gradients. Light is indicative, not a
// filament/adhesive optical simulation. Hanging objects are shown in use, with
// an explicit printing-orientation view for the sacrificial foot.
export function spherePixels(map: PixelMap, grid: Grid, rotation: number, lit: boolean, size = 360, printing = false): Uint8ClampedArray {
  const frame = new Uint8ClampedArray(size * size * 4);
  const hanging = !!grid.hanging && !printing;
  const relief = !!grid.externalRelief;
  const maxR = relief ? (grid.innerRadiusRatio ?? 1) + (grid.maxWallRatio ?? 0) : 1;
  const crown = grid.crownRadiusRatio ?? 0;
  const bottomZ = Math.cos(grid.bottomAngle);
  const extent = Math.max(1, maxR, hanging ? 1 + crown : -bottomZ + (grid.baseHeightRatio ?? 0));
  const radius = size * 0.46 / extent;
  const yaw = rotation * Math.PI / 180;
  const read = (col: number, row: number) => map.data[(Math.min(map.height - 1, Math.max(0, row)) * map.width
    + ((col % map.width) + map.width) % map.width) * 4] / 255;
  const sample = (nx: number, z: number, depth: number) => {
    const r = Math.sqrt(nx * nx + z * z + depth * depth);
    const theta = Math.acos(Math.max(-1, Math.min(1, (hanging ? -z : z) / r)));
    const phi = (Math.atan2(nx, depth) + yaw) * (hanging ? -1 : 1);
    const row = Math.round(latitudeRow(theta, grid.topRows, grid.rows, grid.bottomAngle));
    const col = Math.floor((((phi / (2 * Math.PI) + 0.5) % 1 + 1) % 1) * map.width);
    return { value: read(col, row), col, row };
  };
  for (let y = 0; y < size; y++) {
    const z = (size / 2 - y - 0.5) / radius;
    const base = !hanging && z < bottomZ && z >= bottomZ - (grid.baseHeightRatio ?? 0);
    const cap = hanging && z > -bottomZ;
    const crownZ = Math.sqrt(1 - crown * crown);
    const neck = cap && z > crownZ;
    if (cap && z > crownZ + crown) continue;
    if (!base && !cap && (Math.abs(z) > maxR || (hanging ? -z : z) < bottomZ)) continue;
    for (let x = 0; x < size; x++) {
      const nx = (x + 0.5 - size / 2) / radius;
      let footRadius = grid.baseRadiusRatio ?? 0;
      if (base && grid.hanging && crown > 0) {
        const neckDepth = 0.32 * crown / 5;
        const flareHeight = (grid.baseHeightRatio ?? 0) - 0.4 * crown / 5 - neckDepth;
        const t = Math.max(0, Math.min(1, (bottomZ - neckDepth - z) / flareHeight));
        footRadius = Math.sin(grid.bottomAngle) + t * (footRadius - Math.sin(grid.bottomAngle));
      }
      let square = base ? footRadius ** 2 - nx * nx
        : neck ? crown * crown - nx * nx : (cap ? 1 : maxR) ** 2 - nx * nx - z * z;
      if (square < 0) continue;
      // Opposing thread windows above the spherical dome.
      if (neck && z > crownZ + crown * 0.2 && z < crownZ + crown * 0.8 && Math.abs(nx) < crown * 0.195) continue;
      let depth = Math.sqrt(square);
      let point = sample(nx, z, depth);
      if (relief && !base && !cap) {
        for (let n = 0; n < 5; n++) {
          const r = (grid.innerRadiusRatio ?? 1) + (grid.maxWallRatio ?? 0)
            - ((grid.maxWallRatio ?? 0) - (grid.minWallRatio ?? 0)) * point.value;
          square = r * r - nx * nx - z * z;
          if (square < 0) break;
          depth = Math.sqrt(square); point = sample(nx, z, depth);
        }
        if (square < 0) continue;
      }
      let value = base ? (grid.baseTransmission ?? 0) : cap ? (neck ? 0.1 : grid.capTransmission ?? 1) : point.value;
      // Illustrative local shadow from the internal joint; its sleeve never
      // changes the external silhouette or appears as a projecting pedestal.
      const bodyZ = hanging ? -z : z;
      if (lit && !base && !cap && (grid.fitDepthRatio ?? 0) > 0
        && bodyZ >= bottomZ && bodyZ <= bottomZ + grid.fitDepthRatio!
        && Math.abs(nx) < (grid.fitRadiusRatio ?? 0)) value *= 0.65;
      let normalDepth = base ? depth / footRadius : neck ? depth / crown : depth;
      if (relief && !base && !cap) {
        const slope = ((grid.maxWallRatio ?? 0) - (grid.minWallRatio ?? 0)) * map.width / (2 * Math.PI);
        normalDepth = Math.max(0, Math.min(1, normalDepth
          + (read(point.col + 1, point.row) - read(point.col - 1, point.row)) * slope * 0.6
          + (read(point.col, point.row - 1) - read(point.col, point.row + 1)) * slope * 0.25));
      }
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

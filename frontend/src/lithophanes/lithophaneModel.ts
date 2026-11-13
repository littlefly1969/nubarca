import type { LithophaneSettings } from '@nubarca/api-client';

export const DEFAULT_LITHOPHANE_SETTINGS: LithophaneSettings = {
  diameterMm: 80, minThicknessMm: 0.8, maxThicknessMm: 2.8, openingMm: 20,
  resolutionMm: 0.3, pictureWidthDegrees: 160, pictureHeightDegrees: 110,
  centerX: 0.5, centerY: 0.5, zoom: 1, brightness: 0, contrast: 1, gamma: 1,
};

// Immediate form feedback only. The server validates independently and decides
// the exact mesh. No geometry is generated in the browser.
export function validLithophaneSettings(s: LithophaneSettings): boolean {
  const between = (v: number, min: number, max: number) => Number.isFinite(v) && v >= min && v <= max;
  if (!(between(s.diameterMm, 40, 200) && between(s.minThicknessMm, 0.4, 2)
    && between(s.maxThicknessMm, s.minThicknessMm + 0.1, 5) && between(s.openingMm, 4, s.diameterMm * 0.65)
    && between(s.resolutionMm, 0.15, 1) && between(s.pictureWidthDegrees, 40, 300)
    && between(s.pictureHeightDegrees, 30, 140) && between(s.centerX, 0, 1) && between(s.centerY, 0, 1)
    && between(s.zoom, 0.01, 4) && between(s.brightness, -0.5, 0.5)
    && between(s.contrast, 0.25, 3) && between(s.gamma, 0.3, 3))) return false;
  const radius = s.diameterMm / 2;
  const columns = 4 * Math.ceil(Math.PI * s.diameterMm / s.resolutionMm / 4);
  const top = Math.ceil(Math.PI * radius / 2 / s.resolutionMm);
  const innerRadius = radius - s.minThicknessMm;
  const bottom = Math.acos(-Math.sqrt(innerRadius ** 2 - (s.openingMm / 2) ** 2) / radius);
  const rows = top + Math.ceil((bottom - Math.PI / 2) * radius / s.resolutionMm);
  return 4 * columns * rows <= 2_000_000;
}

export const activeLithophane = (status: string) => status === 'queued' || status === 'running' || status === 'cancelling';

/** Inverse of the server's latitude grid, including its exact equator. */
export function latitudeRow(theta: number, topRows: number, rows: number, bottomAngle: number): number {
  return theta <= Math.PI / 2 ? theta / (Math.PI / 2) * topRows
    : topRows + (theta - Math.PI / 2) / (bottomAngle - Math.PI / 2) * (rows - topRows);
}

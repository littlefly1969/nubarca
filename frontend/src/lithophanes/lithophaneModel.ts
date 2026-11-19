import type { LithophaneSettings } from '@nubarca/api-client';

export const DEFAULT_LITHOPHANE_SETTINGS: LithophaneSettings = {
  diameterMm: 80, minThicknessMm: 0.8, maxThicknessMm: 2.8, openingMm: 20,
  baseCollar: true,
  externalRelief: true, hanging: false, printAnchor: false, frameAspect: 4 / 3, pictureScale: 0.8,
  resolutionMm: 0.3, pictureWidthDegrees: 160, pictureHeightDegrees: 110,
  centerX: 0.5, centerY: 0.5, zoom: 1, brightness: 0, contrast: 1, gamma: 1, backgroundThicknessMm: 0.8,
};

const MAX_TRIANGLES = 2_000_000;

export const LITHOPHANE_TEXT_MAX_LENGTH = 60;
export const LITHOPHANE_TEXT_MAX_LINES = 2;
export const DEFAULT_LITHOPHANE_TEXT_HEIGHT = 8;

/** Mirrors the server: one or two lines, no other control characters, no emoji. */
export function validLithophaneText(text: string | null | undefined): boolean {
  if (!text) return true;
  if (text.length > LITHOPHANE_TEXT_MAX_LENGTH) return false;
  if (/[\uD800-\uDFFF]/.test(text)) return false;
  if (/[\u0000-\u0009\u000B-\u001F\u007F-\u009F]/.test(text)) return false;
  return text.split('\n').length <= LITHOPHANE_TEXT_MAX_LINES;
}

export const hasLithophaneText = (s: LithophaneSettings) => (s.text ?? '').trim().length > 0;

/**
 * What is sent: without words, no text fields at all, so a project without
 * text is stored exactly as before text existed; the default face and
 * position are sent as absent for the same reason.
 */
export function lithophaneRequestSettings(s: LithophaneSettings): LithophaneSettings {
  const { text, textFont, textHeightMm, textPosition, ...rest } = s;
  if (!hasLithophaneText(s)) return rest;
  return {
    ...rest, text: text!.replace(/\r/g, ''),
    ...(textFont && textFont !== 'nubarca' ? { textFont } : {}),
    textHeightMm: textHeightMm ?? DEFAULT_LITHOPHANE_TEXT_HEIGHT,
    ...(textPosition && textPosition !== 'below' ? { textPosition } : {}),
  };
}

// Immediate form feedback only. The server validates independently and decides
// the exact mesh. No geometry is generated in the browser.
function withinBounds(s: LithophaneSettings): boolean {
  const between = (v: number, min: number, max: number) => Number.isFinite(v) && v >= min && v <= max;
  return between(s.diameterMm, 40, 200) && between(s.minThicknessMm, 0.4, 2)
    && (s.backgroundThicknessMm == null || between(s.backgroundThicknessMm, 0.4, 2))
    && between(s.maxThicknessMm, s.minThicknessMm + 0.1, 5) && between(s.openingMm, 4, s.diameterMm * 0.65)
    && (!s.hanging || (!!s.externalRelief && between(s.openingMm, 16, s.diameterMm * 0.65)
      && (s.backgroundThicknessMm ?? s.minThicknessMm) >= 0.8))
    && (!s.internalFit || (!!s.hanging && !s.printAnchor))
    && (!s.printBrim || !!s.internalFit)
    && (s.frameAspect == null || between(s.frameAspect, 0.2, 5))
    && (s.pictureScale == null || (s.frameAspect != null && between(s.pictureScale, 0.2, 1)))
    && between(s.resolutionMm, 0.15, 1) && between(s.pictureWidthDegrees, 40, 300)
    && between(s.pictureHeightDegrees, 30, 140) && between(s.centerX, 0, 1) && between(s.centerY, 0, 1)
    && between(s.zoom, 0.01, 4) && between(s.brightness, -0.5, 0.5)
    && between(s.contrast, 0.25, 3) && between(s.gamma, 0.3, 3)
    && validLithophaneText(s.text) && (s.textHeightMm == null || between(s.textHeightMm, 3, 30));
}

/** Same triangle count as the server's LithophaneGrid (parity-tested on both sides). */
export function lithophaneGrid(s: LithophaneSettings) {
  const radius = s.diameterMm / 2;
  const columns = 4 * Math.ceil(Math.PI * s.diameterMm / s.resolutionMm / 4);
  const top = Math.ceil(Math.PI * radius / 2 / s.resolutionMm);
  const innerRadius = radius - (s.externalRelief ? (s.backgroundThicknessMm ?? s.minThicknessMm) : s.minThicknessMm);
  let bottomZ = -Math.sqrt(innerRadius ** 2 - (s.openingMm / 2) ** 2);
  if (s.baseCollar && !s.hanging) {
    const baseRadius = Math.max(Math.sqrt(radius ** 2 - bottomZ ** 2),
      s.openingMm / 2 + Math.min(5, Math.max(1.2, s.openingMm * 0.2)));
    bottomZ = -Math.sqrt(radius ** 2 - baseRadius ** 2);
  }
  const bottom = Math.acos(bottomZ / radius);
  const rows = top + Math.ceil((bottom - Math.PI / 2) * radius / s.resolutionMm);
  const rimSegments = s.internalFit ? (s.printBrim ? 6 : 4)
    : s.hanging ? (s.printAnchor ? 6 : 1) : s.baseCollar ? 3 : 1;
  return { radius, columns, top, bottom, rows, triangles: 4 * columns * rows + 2 * columns * (rimSegments - 1) };
}

export const lithophaneTriangleCount = (s: LithophaneSettings) => lithophaneGrid(s).triangles;

/** The same aspect-preserving crop area as the server; zoom never crops it again. */
export function lithophanePhotoArea(s: LithophaneSettings) {
  if (s.frameAspect == null) return { width: s.pictureWidthDegrees, height: s.pictureHeightDegrees };
  const grid = lithophaneGrid(s);
  const available = 2 * (grid.bottom * 180 / Math.PI - 90) - 4 / grid.radius * 180 / Math.PI - 4;
  const height = Math.min(140, 280 / s.frameAspect, available) * (s.pictureScale ?? 0.8);
  return { width: height * s.frameAspect, height };
}

export function validLithophaneSettings(s: LithophaneSettings): boolean {
  return withinBounds(s) && lithophaneTriangleCount(s) <= MAX_TRIANGLES;
}

/**
 * When every value is within its limits but the surface is too detailed for
 * the triangle budget (a large sphere at a fine step), the finest step in the
 * form's 0.05 mm increments that fits. Null when that is not the problem.
 */
export function suggestedLithophaneResolution(s: LithophaneSettings): number | null {
  if (!withinBounds(s) || lithophaneTriangleCount(s) <= MAX_TRIANGLES) return null;
  for (let hundredths = Math.max(15, Math.ceil(s.resolutionMm * 20) * 5); hundredths <= 100; hundredths += 5) {
    const resolutionMm = hundredths / 100;
    if (lithophaneTriangleCount({ ...s, resolutionMm }) <= MAX_TRIANGLES) return resolutionMm;
  }
  return null;
}

export const activeLithophane = (status: string) => status === 'queued' || status === 'running' || status === 'cancelling';

/** Inverse of the server's latitude grid, including its exact equator. */
export function latitudeRow(theta: number, topRows: number, rows: number, bottomAngle: number): number {
  return theta <= Math.PI / 2 ? theta / (Math.PI / 2) * topRows
    : topRows + (theta - Math.PI / 2) / (bottomAngle - Math.PI / 2) * (rows - topRows);
}

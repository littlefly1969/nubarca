import { api } from './client';

export interface LithophaneSettings {
  diameterMm: number;
  minThicknessMm: number;
  maxThicknessMm: number;
  openingMm: number;
  /** Straight annular base proportional to the opening. Absent: legacy sphere. */
  baseCollar?: boolean;
  externalRelief?: boolean;
  hanging?: boolean;
  printAnchor?: boolean;
  /** 1 mm internal socket, 2 mm deep, with a matching straight cap spigot. */
  internalFit?: boolean;
  /** Removable 0.16 mm brims inside both body and cap. Requires internalFit. */
  printBrim?: boolean;
  /** Width/height of the independent 2D crop frame. Absent: legacy framing. */
  frameAspect?: number | null;
  /** Fraction of the largest crop area that fits away from the opening (0.2–1). */
  pictureScale?: number | null;
  resolutionMm: number;
  pictureWidthDegrees: number;
  pictureHeightDegrees: number;
  centerX: number;
  centerY: number;
  zoom: number;
  brightness: number;
  contrast: number;
  gamma: number;
  /** Wall outside the photograph. Null only in projects saved before it existed (= minThicknessMm). */
  backgroundThicknessMm?: number | null;
  /** The owner's words (one or two lines, \n between them), drawn as the thickest wall. Absent: none. */
  text?: string | null;
  /** A text font id (frontend/src/text/textFonts.ts). Absent: the default. */
  textFont?: string | null;
  /** Height of a line of capitals on the sphere, in mm (3–30). Absent: 8. */
  textHeightMm?: number | null;
  /** Where the words go with a photo. Absent: below. */
  textPosition?: 'below' | 'above' | 'onPhoto' | null;
}

export interface LithophaneSummary {
  id: string;
  name: string;
  fileItemId: string | null;
  settings: LithophaneSettings;
  status: 'queued' | 'running' | 'ready' | 'failed' | 'cancelled' | 'cancelling';
  completedRows: number;
  totalRows: number;
  triangleCount: number;
  sizeBytes: number;
  createdAt: string;
  downloadReady: boolean;
}
export interface LithophanePage { items: LithophaneSummary[]; nextCursor: string | null }
export interface LithophanePreview {
  imageBase64: string;
  columns: number;
  rows: number;
  topRows: number;
  bottomAngle: number;
  triangleCount: number;
  sizeBytes: number;
  /** Fresh previews only: the lowest zoom that still changes this photo's map. */
  minimumZoom?: number | null;
  baseHeightRatio?: number;
  baseRadiusRatio?: number;
  baseTransmission?: number;
  hanging?: boolean;
  externalRelief?: boolean;
  innerRadiusRatio?: number;
  minWallRatio?: number;
  maxWallRatio?: number;
  crownRadiusRatio?: number;
  capTransmission?: number;
  fitDepthRatio?: number;
  fitRadiusRatio?: number;
}

const base = '/api/lithophanes';
export const listLithophanes = (cursor?: string | null, signal?: AbortSignal) =>
  api<LithophanePage>(`${base}${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`, { signal });
/** fileItemId null: a lithophane of words only (settings.text is then required). */
export const createLithophane = (fileItemId: string | null, settings: LithophaneSettings, requestId: string, signal?: AbortSignal) =>
  api<LithophaneSummary>(base, { method: 'POST', json: { fileItemId, settings, requestId }, signal });
export const getLithophane = (id: string, signal?: AbortSignal) =>
  api<LithophaneSummary>(`${base}/${encodeURIComponent(id)}`, { signal });
export const getLithophaneStatuses = (ids: string[], signal?: AbortSignal) =>
  api<LithophaneSummary[]>(`${base}/statuses`, { method: 'POST', json: { ids }, signal });
export const previewLithophane = (fileItemId: string | null, settings: LithophaneSettings, signal?: AbortSignal) =>
  api<LithophanePreview>(`${base}/preview`, { method: 'POST', json: { fileItemId, settings }, signal });
export const getLithophanePreview = (id: string, signal?: AbortSignal) =>
  api<LithophanePreview>(`${base}/${encodeURIComponent(id)}/preview`, { signal });
export const cancelLithophane = (id: string, signal?: AbortSignal) =>
  api<void>(`${base}/${encodeURIComponent(id)}/cancel`, { method: 'POST', signal });
export const deleteLithophane = (id: string, signal?: AbortSignal) =>
  api<void>(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE', signal });
export const lithophaneDownloadUrl = (id: string) => `${base}/${encodeURIComponent(id)}/download`;
export const lithophaneCapUrl = (id: string) => `${base}/${encodeURIComponent(id)}/cap`;

// Photographs the owner sent here from the gallery, a list or an album, waiting
// to become lithophanes. References only: the thumbnail is the small derivative.
export interface LithophaneQueueEntry { fileItemId: string; name: string; thumbnailUrl: string; addedAt: string }
export interface LithophaneQueueResult { added: string[]; skipped: { itemId: string; reason: string }[] }
export const listLithophaneQueue = (signal?: AbortSignal) =>
  api<LithophaneQueueEntry[]>(`${base}/queue`, { signal });
export const addToLithophaneQueue = (fileItemIds: string[], signal?: AbortSignal) =>
  api<LithophaneQueueResult>(`${base}/queue`, { method: 'POST', json: { fileItemIds }, signal });
export const removeFromLithophaneQueue = (fileItemId: string, signal?: AbortSignal) =>
  api<void>(`${base}/queue/${encodeURIComponent(fileItemId)}`, { method: 'DELETE', signal });

import { api } from './client';

export interface LithophaneSettings {
  diameterMm: number;
  minThicknessMm: number;
  maxThicknessMm: number;
  openingMm: number;
  resolutionMm: number;
  pictureWidthDegrees: number;
  pictureHeightDegrees: number;
  centerX: number;
  centerY: number;
  zoom: number;
  brightness: number;
  contrast: number;
  gamma: number;
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
}

const base = '/api/lithophanes';
export const listLithophanes = (cursor?: string | null, signal?: AbortSignal) =>
  api<LithophanePage>(`${base}${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`, { signal });
export const createLithophane = (fileItemId: string, settings: LithophaneSettings, requestId: string, signal?: AbortSignal) =>
  api<LithophaneSummary>(base, { method: 'POST', json: { fileItemId, settings, requestId }, signal });
export const getLithophane = (id: string, signal?: AbortSignal) =>
  api<LithophaneSummary>(`${base}/${encodeURIComponent(id)}`, { signal });
export const getLithophaneStatuses = (ids: string[], signal?: AbortSignal) =>
  api<LithophaneSummary[]>(`${base}/statuses`, { method: 'POST', json: { ids }, signal });
export const previewLithophane = (fileItemId: string, settings: LithophaneSettings, signal?: AbortSignal) =>
  api<LithophanePreview>(`${base}/preview`, { method: 'POST', json: { fileItemId, settings }, signal });
export const getLithophanePreview = (id: string, signal?: AbortSignal) =>
  api<LithophanePreview>(`${base}/${encodeURIComponent(id)}/preview`, { signal });
export const cancelLithophane = (id: string, signal?: AbortSignal) =>
  api<void>(`${base}/${encodeURIComponent(id)}/cancel`, { method: 'POST', signal });
export const deleteLithophane = (id: string, signal?: AbortSignal) =>
  api<void>(`${base}/${encodeURIComponent(id)}`, { method: 'DELETE', signal });
export const lithophaneDownloadUrl = (id: string) => `${base}/${encodeURIComponent(id)}/download`;

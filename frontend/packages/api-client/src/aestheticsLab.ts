import { api } from './client';

// Owner-private Aesthetics Lab (Laboratorio estetico). DTOs mirror the backend's
// display-safe shape ONLY — no blobObjectId, storageKey, ownerUserId, sha256,
// path, container key, or raw model output is ever present. Media is reached
// through the authenticated, owner-scoped URLs the backend supplies.

export interface AestheticLabItem {
  id: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  latestRunStatus: string | null;
  latestRunErrorCode: string | null;
  overallScore: number | null;
  profileKey: string;
  thumbnailUrl: string;
  previewUrl: string;
}

export interface AestheticMetric {
  key: string;
  group: string;
  value: number;
  scaleMin: number;
  scaleMax: number;
  confidence: number | null;
  version: number;
}

export interface AestheticText {
  kind: string;
  language: string;
  text: string;
  promptTemplateVersion: number | null;
}

export interface AestheticRun {
  id: string;
  status: string;
  profileKey: string;
  modelName: string | null;
  modelRevision: string | null;
  runtimeName: string | null;
  runtimeVersion: string | null;
  preprocessingProfileKey: string;
  requestedCapabilities: string[];
  completedCapabilities: string[];
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  durationMs: number | null;
  errorCode: string | null;
  warnings: string[];
  metrics: AestheticMetric[];
  texts: AestheticText[];
}

export interface AestheticRunSummary {
  id: string;
  status: string;
  createdAt: string;
  completedAt: string | null;
  durationMs: number | null;
  errorCode: string | null;
  completedCapabilities: string[];
  overallScore: number | null;
}

export interface AestheticLabItemDetail {
  id: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  previewUrl: string;
  latestRun: AestheticRun | null;
  history: AestheticRunSummary[];
}

export interface AestheticLabPage {
  items: AestheticLabItem[];
  nextCursor: string | null;
}

export interface AestheticAnalysisEnqueued {
  itemId: string;
  runId: string;
  status: string;
}

export interface AestheticAnalysisSkipped {
  itemId: string;
  reason: string;
}

export interface AestheticAnalysisBatchResult {
  enqueued: AestheticAnalysisEnqueued[];
  skipped: AestheticAnalysisSkipped[];
}

export interface AestheticAddFromGalleryResult {
  added: AestheticLabItem[];
  skipped: { itemId: string; reason: string }[];
}

export function listAestheticLabItems(
  cursor?: string | null,
  limit?: number,
  signal?: AbortSignal,
): Promise<AestheticLabPage> {
  const params = new URLSearchParams();
  if (cursor) params.set('cursor', cursor);
  if (limit) params.set('limit', String(limit));
  const qs = params.toString();
  return api<AestheticLabPage>(`/api/aesthetics-lab/items${qs ? `?${qs}` : ''}`, { signal });
}

export function getAestheticLabItem(id: string, signal?: AbortSignal): Promise<AestheticLabItemDetail> {
  return api<AestheticLabItemDetail>(`/api/aesthetics-lab/items/${id}`, { signal });
}

export function addAestheticLabFromGallery(
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<AestheticAddFromGalleryResult> {
  return api<AestheticAddFromGalleryResult>('/api/aesthetics-lab/items/from-gallery', {
    method: 'POST',
    json: { fileItemIds },
    signal,
  });
}

// Uploads a single image directly into the lab (never becomes a gallery file).
export function uploadAestheticLabItem(file: File, signal?: AbortSignal): Promise<AestheticLabItem> {
  const form = new FormData();
  form.append('file', file, file.name);
  return api<AestheticLabItem>('/api/aesthetics-lab/items/upload', {
    method: 'POST',
    formData: form,
    signal,
  });
}

export function removeAestheticLabItem(id: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/aesthetics-lab/items/${id}`, { method: 'DELETE', signal });
}

// Requests analysis of a bounded batch. Returns quickly — one run + one durable
// job PER item runs on the worker, never in this request.
export function requestAestheticAnalysis(
  itemIds: string[],
  capabilities?: string[],
  signal?: AbortSignal,
): Promise<AestheticAnalysisBatchResult> {
  return api<AestheticAnalysisBatchResult>('/api/aesthetics-lab/analyses', {
    method: 'POST',
    json: { itemIds, capabilities: capabilities ?? null },
    signal,
  });
}

export function getAestheticRun(id: string, signal?: AbortSignal): Promise<AestheticRun> {
  return api<AestheticRun>(`/api/aesthetics-lab/runs/${id}`, { signal });
}

export function cancelAestheticRun(id: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/aesthetics-lab/runs/${id}/cancel`, { method: 'POST', signal });
}

export function retryAestheticRun(id: string, signal?: AbortSignal): Promise<AestheticRun> {
  return api<AestheticRun>(`/api/aesthetics-lab/runs/${id}/retry`, { method: 'POST', signal });
}

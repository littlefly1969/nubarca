import { api } from './client';

// Owner-private Plates (Targhe) surface. DTOs mirror the backend's display-safe
// shape ONLY — no blobObjectId, storageKey, ownerUserId, sha256, path, or
// container key is ever present. Media is reached through the authenticated,
// owner-scoped URLs the backend supplies.

export interface PlateImageListItem {
  id: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  status: string;
  analysisStatus: string;
  platesCount: number;
  createdAt: string;
  updatedAt: string;
  thumbnailUrl: string;
  previewUrl: string;
}

export interface PlateBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface PlateDetection {
  id: string;
  text: string;
  normalizedText: string;
  confidence: number;
  plateConfidence: number;
  ocrConfidence: number;
  countryHint: string | null;
  regionHint: string | null;
  bbox: PlateBox;
}

export interface PlateAnalysisSummary {
  platesCount: number;
  facesRedactedAvailable: boolean;
  analysisStatus: string;
  latestJobId: string | null;
  lastAnalyzedAt: string | null;
}

// Safe, owner-only redaction summary. Carries NO face boxes/coordinates —
// redaction is baked into the served media, so the UI never needs them.
export interface PlateRedactionInfo {
  available: boolean;
  facesCount: number;
  profileKey: string;
}

export interface PlateImageDetail {
  id: string;
  originalFileName: string;
  contentType: string;
  width: number | null;
  height: number | null;
  sizeBytes: number;
  status: string;
  createdAt: string;
  updatedAt: string;
  previewUrl: string;
  originalUrl: string;
  analysisSummary: PlateAnalysisSummary;
  detections: PlateDetection[];
  redaction: PlateRedactionInfo;
}

// Appends the server-side face-redaction flag to a plate media URL. When
// `blurFaces` is true the backend serves an image with detected faces redacted
// (never the unredacted image); when false it serves the normal owner-private
// rendition. The base URL is one the backend supplied (preview/original/
// thumbnail) — no internals are constructed client-side.
export function withBlurFaces(url: string, blurFaces: boolean): string {
  if (!blurFaces) {
    return url;
  }
  return url.includes('?') ? `${url}&blurFaces=true` : `${url}?blurFaces=true`;
}

export interface PlateAnalysisJobSummary {
  id: string;
  status: string;
  analysisStatus: string;
  profileKey: string;
  platesCount: number;
  requestedAt: string;
  startedAt: string | null;
  completedAt: string | null;
  failedAt: string | null;
  errorCode: string | null;
  lastAnalyzedAt: string | null;
}

export function listPlateImages(signal?: AbortSignal): Promise<PlateImageListItem[]> {
  return api<PlateImageListItem[]>('/api/plates/images', { signal });
}

export function getPlateImage(id: string, signal?: AbortSignal): Promise<PlateImageDetail> {
  return api<PlateImageDetail>(`/api/plates/images/${id}`, { signal });
}

// Uploads a single plate image. The caller loops for multi-file selection so
// each file gets its own status/error, mirroring the library UploadPanel.
export function uploadPlateImage(file: File, signal?: AbortSignal): Promise<PlateImageListItem> {
  const form = new FormData();
  form.append('file', file, file.name);
  return api<PlateImageListItem>('/api/plates/images', {
    method: 'POST',
    formData: form,
    signal,
  });
}

export function deletePlateImage(id: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/plates/images/${id}`, { method: 'DELETE', signal });
}

// Partial result of adding gallery images into Plates. `skipped` carries a
// client-safe reason code per fileItem (e.g. "not_an_image"); no existence leak.
export interface PlateAddFromGalleryResult {
  added: PlateImageListItem[];
  skipped: { itemId: string; reason: string }[];
}

// Adds EXISTING owner gallery images into the hidden plates container by
// fileItemId. No bytes are copied (blob reference acquire), idempotent on active
// membership, and analysis is NOT started. Mirrors addAestheticLabFromGallery.
export function addPlateImagesFromGallery(
  fileItemIds: string[],
  signal?: AbortSignal,
): Promise<PlateAddFromGalleryResult> {
  return api<PlateAddFromGalleryResult>('/api/plates/images/from-gallery', {
    method: 'POST',
    json: { fileItemIds },
    signal,
  });
}

// Requests owner-private ALPR analysis. Returns quickly with a job summary — the
// detection + OCR run on the worker, never in this request.
export function requestPlateAnalysis(
  id: string,
  signal?: AbortSignal,
): Promise<PlateAnalysisJobSummary> {
  return api<PlateAnalysisJobSummary>(`/api/plates/images/${id}/analysis`, {
    method: 'POST',
    signal,
  });
}

export function getPlateAnalysisLatest(
  id: string,
  signal?: AbortSignal,
): Promise<PlateAnalysisSummary> {
  return api<PlateAnalysisSummary>(`/api/plates/images/${id}/analysis/latest`, { signal });
}

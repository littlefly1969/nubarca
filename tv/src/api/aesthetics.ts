// TV "Beauty Lab" (Laboratorio bellezza) API — the grant-gated projection of the
// owner-private Aesthetics Lab. Every call carries the SAME in-memory personal
// unlock grant (personalHeaders()) the Personal Area uses; the server hits the
// SAME application services the web lab does, so behaviour is identical. DTOs are
// display-safe (no blob id / storage key / SHA / raw model output); media is
// derived-only (loaded via loadTvMedia with personal:true), never originals.

import { tvDelete, tvGet, tvPost } from './client';
import { personalHeaders } from './personal';

export interface BeautyLabItem {
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

export interface BeautyLabMetric {
  key: string;
  group: string;
  value: number;
  scaleMin: number;
  scaleMax: number;
  confidence: number | null;
  version: number;
}

export interface BeautyLabRun {
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
  metrics: BeautyLabMetric[];
}

export interface BeautyLabRunSummary {
  id: string;
  status: string;
  createdAt: string;
  completedAt: string | null;
  durationMs: number | null;
  errorCode: string | null;
  completedCapabilities: string[];
  overallScore: number | null;
}

export interface BeautyLabItemDetail {
  id: string;
  originalFileName: string;
  contentType: string;
  sizeBytes: number;
  width: number | null;
  height: number | null;
  createdAt: string;
  previewUrl: string;
  latestRun: BeautyLabRun | null;
  history: BeautyLabRunSummary[];
}

export interface BeautyLabPage {
  items: BeautyLabItem[];
  nextCursor: string | null;
}

export interface BeautyLabAnalysisResult {
  enqueued: { itemId: string; runId: string; status: string }[];
  skipped: { itemId: string; reason: string }[];
}

export interface BeautyLabUploadSession {
  id: string;
  uploadUrl: string;
  expiresAt: string;
  maxFiles: number;
  maxTotalBytes: number;
  accepted: number;
  rejected: number;
  status: string;
}

export interface BeautyLabUploadSessionStatus {
  id: string;
  expiresAt: string;
  maxFiles: number;
  maxTotalBytes: number;
  accepted: number;
  rejected: number;
  status: string;
}

export function listBeautyLabItems(cursor?: string | null, limit = 50): Promise<BeautyLabPage> {
  const params = new URLSearchParams();
  if (cursor) params.set('cursor', cursor);
  params.set('limit', String(limit));
  return tvGet<BeautyLabPage>(`/api/tv/personal/aesthetics/items?${params.toString()}`, personalHeaders());
}

export function getBeautyLabItem(id: string): Promise<BeautyLabItemDetail> {
  return tvGet<BeautyLabItemDetail>(`/api/tv/personal/aesthetics/items/${encodeURIComponent(id)}`, personalHeaders());
}

export function requestBeautyLabAnalysis(itemIds: string[]): Promise<BeautyLabAnalysisResult> {
  return tvPost<BeautyLabAnalysisResult>(
    '/api/tv/personal/aesthetics/analyses',
    { itemIds, capabilities: null },
    personalHeaders(),
  );
}

export function cancelBeautyLabRun(runId: string): Promise<void> {
  return tvPost<void>(
    `/api/tv/personal/aesthetics/runs/${encodeURIComponent(runId)}/cancel`,
    undefined,
    personalHeaders(),
  );
}

export function retryBeautyLabRun(runId: string): Promise<BeautyLabRun> {
  return tvPost<BeautyLabRun>(
    `/api/tv/personal/aesthetics/runs/${encodeURIComponent(runId)}/retry`,
    undefined,
    personalHeaders(),
  );
}

export function removeBeautyLabItem(id: string): Promise<void> {
  return tvDelete<void>(`/api/tv/personal/aesthetics/items/${encodeURIComponent(id)}`, personalHeaders());
}

export function createBeautyLabUploadSession(): Promise<BeautyLabUploadSession> {
  return tvPost<BeautyLabUploadSession>('/api/tv/personal/aesthetics/upload-sessions', undefined, personalHeaders());
}

export function getBeautyLabUploadSession(id: string): Promise<BeautyLabUploadSessionStatus> {
  return tvGet<BeautyLabUploadSessionStatus>(
    `/api/tv/personal/aesthetics/upload-sessions/${encodeURIComponent(id)}`,
    personalHeaders(),
  );
}

export function revokeBeautyLabUploadSession(id: string): Promise<void> {
  return tvPost<void>(
    `/api/tv/personal/aesthetics/upload-sessions/${encodeURIComponent(id)}/revoke`,
    undefined,
    personalHeaders(),
  );
}

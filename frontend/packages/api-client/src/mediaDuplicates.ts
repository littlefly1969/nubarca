import { api } from './client';

export interface MediaDuplicateCleanupStart {
  runId: string;
  jobId: string;
  status: string;
}

export interface MediaDuplicateCleanupStatus {
  runId: string;
  status: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled';
  duplicateGroupCount: number;
  filesRemovedCount: number;
  filesRetainedCount: number;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

const BASE = '/api/cloud-functions/media-duplicates/exact/runs';

export function startExactMediaDuplicateCleanup(signal?: AbortSignal): Promise<MediaDuplicateCleanupStart> {
  return api<MediaDuplicateCleanupStart>(BASE, { method: 'POST', signal });
}

export function getExactMediaDuplicateCleanupStatus(
  runId: string,
  signal?: AbortSignal,
): Promise<MediaDuplicateCleanupStatus> {
  return api<MediaDuplicateCleanupStatus>(`${BASE}/${runId}`, { signal });
}

export const MEDIA_DUPLICATE_CLEANUP_TERMINAL = new Set(['succeeded', 'failed', 'cancelled']);

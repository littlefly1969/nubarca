import { api } from './client';

// Owner-private photo-archive export (Cloud Functions). A session snapshots the
// visible photo library into a stable manifest built by a background job, then
// serves per-file ORIGINAL content. No ZIP. The token is returned ONCE at
// creation and is used (as an Authorization: Bearer header) by the download
// command; only its hash is stored server-side. DTOs are sanitized — never any
// storage key, blob id, SHA, or physical path.

export interface PhotoExportCreated {
  sessionId: string;
  // Raw token — shown once so the user can build the download command.
  token: string;
  status: string;
  expiresAt: string;
}

export interface PhotoExportStatus {
  sessionId: string;
  // pending | building | ready | failed | revoked | expired
  status: string;
  fileCount: number;
  totalBytes: number;
  errorSummary: string | null;
  createdAt: string;
  completedAt: string | null;
  expiresAt: string;
  manifestReady: boolean;
}

export function createPhotoExport(signal?: AbortSignal): Promise<PhotoExportCreated> {
  return api<PhotoExportCreated>('/api/photo-exports', { method: 'POST', signal });
}

export function getPhotoExport(
  sessionId: string,
  signal?: AbortSignal,
): Promise<PhotoExportStatus> {
  return api<PhotoExportStatus>(
    `/api/photo-exports/${encodeURIComponent(sessionId)}`,
    { signal },
  );
}

export function revokePhotoExport(sessionId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/photo-exports/${encodeURIComponent(sessionId)}`, {
    method: 'DELETE',
    signal,
  });
}

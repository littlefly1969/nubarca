import { api } from './client';

// Slice 93 — web remote-staging upload client. Mirrors the backend DTOs in
// NubArca.Api.Uploads. Every shape is safe: relative paths and stable
// categories only — no absolute server paths, storage keys, hashes, blob ids,
// or payloads ever appear here.

export interface StagingConfig {
  enabled: boolean;
  maxSessionBytes: number;
  maxFileBytes: number;
  maxFilesPerSession: number;
  chunkSizeBytes: number;
  sessionTtlHours: number;
}

export interface StagingImportProgress {
  status: string;
  phase: string | null;
  importedFiles: number;
  pendingFiles: number;
  failedFiles: number;
  conflictFiles: number;
  skippedFiles: number;
  // deleted-content-import-skip: disjoint from skippedFiles.
  skippedPreviouslyDeletedFiles: number;
  skippedAlreadyPresentFiles: number;
  importedBytes: number;
}

export interface StagingSession {
  sessionId: string;
  name: string;
  status: string;
  targetUserId: string;
  destinationFolderId: string | null;
  totalFiles: number;
  totalBytes: number;
  receivedFiles: number;
  receivedBytes: number;
  verifiedFiles: number;
  failedFiles: number;
  chunkSizeBytes: number;
  createdAt: string;
  expiresAt: string;
  completedAt: string | null;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  adminImportRunId: string | null;
  import: StagingImportProgress | null;
}

export interface StagingSessionList {
  sessions: StagingSession[];
  total: number;
}

export interface StagingManifestFile {
  relativePath: string;
  sizeBytes: number;
  lastModifiedAt: string | null;
}

export interface StagingManifestResult {
  sessionId: string;
  status: string;
  totalFiles: number;
  totalBytes: number;
  chunkSizeBytes: number;
  alreadyCompleteFiles: number;
}

export interface StagingMissingItem {
  itemId: string;
  ordinal: number;
  relativePath: string;
  sizeBytes: number;
  lastModifiedAt: string | null;
  missingChunks: number[];
}

export interface StagingMissingResult {
  sessionId: string;
  chunkSizeBytes: number;
  items: StagingMissingItem[];
  nextAfterOrdinal: number | null;
  hasMore: boolean;
}

export interface StagingChunkResult {
  itemId: string;
  chunkIndex: number;
  alreadyReceived: boolean;
  itemStatus: string;
  receivedChunkCount: number;
  expectedChunkCount: number;
}

export interface StagingVerifyResult {
  sessionId: string;
  status: string;
  verifiedFiles: number;
  incompleteFiles: number;
  corruptFiles: number;
  readyToImport: boolean;
}

export interface StagingImportStartResult {
  sessionId: string;
  status: string;
  adminImportRunId: string;
  jobId: string;
}

export interface StagingCancelResult {
  sessionId: string;
  status: string;
  cancellationRequested: boolean;
}

export function getStagingConfig(signal?: AbortSignal): Promise<StagingConfig> {
  return api<StagingConfig>('/api/uploads/staging/config', { signal });
}

export function createStagingSession(
  body: {
    name?: string;
    targetUserId?: string;
    destinationFolderId?: string | null;
    // deleted-content-import-skip: import options applied server-side.
    skipPreviouslyDeleted?: boolean;
    skipExistingContent?: boolean;
  },
  signal?: AbortSignal,
): Promise<StagingSession> {
  return api<StagingSession>('/api/uploads/staging/sessions', { method: 'POST', json: body, signal });
}

export function listStagingSessions(signal?: AbortSignal): Promise<StagingSessionList> {
  return api<StagingSessionList>('/api/uploads/staging/sessions', { signal });
}

export function getStagingSession(sessionId: string, signal?: AbortSignal): Promise<StagingSession> {
  return api<StagingSession>(`/api/uploads/staging/sessions/${sessionId}`, { signal });
}

export function submitStagingManifest(
  sessionId: string,
  files: StagingManifestFile[],
  signal?: AbortSignal,
): Promise<StagingManifestResult> {
  return api<StagingManifestResult>(
    `/api/uploads/staging/sessions/${sessionId}/manifest`,
    { method: 'POST', json: { files }, signal },
  );
}

export function getStagingMissing(
  sessionId: string,
  afterOrdinal = 0,
  signal?: AbortSignal,
): Promise<StagingMissingResult> {
  const params = new URLSearchParams({ afterOrdinal: String(afterOrdinal) });
  return api<StagingMissingResult>(
    `/api/uploads/staging/sessions/${sessionId}/missing?${params.toString()}`,
    { signal },
  );
}

export function putStagingChunk(
  sessionId: string,
  itemId: string,
  chunkIndex: number,
  chunk: Blob,
  signal?: AbortSignal,
): Promise<StagingChunkResult> {
  return api<StagingChunkResult>(
    `/api/uploads/staging/sessions/${sessionId}/items/${itemId}/chunks/${chunkIndex}`,
    { method: 'PUT', rawBody: chunk, signal },
  );
}

export function verifyStagingSession(sessionId: string, signal?: AbortSignal): Promise<StagingVerifyResult> {
  return api<StagingVerifyResult>(
    `/api/uploads/staging/sessions/${sessionId}/verify`,
    { method: 'POST', signal },
  );
}

export function startStagingImport(
  sessionId: string,
  signal?: AbortSignal,
): Promise<StagingImportStartResult> {
  return api<StagingImportStartResult>(
    `/api/uploads/staging/sessions/${sessionId}/import`,
    { method: 'POST', signal },
  );
}

export function cancelStagingSession(sessionId: string, signal?: AbortSignal): Promise<StagingCancelResult> {
  return api<StagingCancelResult>(
    `/api/uploads/staging/sessions/${sessionId}/cancel`,
    { method: 'POST', signal },
  );
}

export function deleteStagingSession(sessionId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/uploads/staging/sessions/${sessionId}`, { method: 'DELETE', signal });
}

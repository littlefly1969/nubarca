import { api } from './client';

// Slice 81 — admin server-side import client. Mirrors the backend DTOs in
// NubArca.Api.Admin. Every shape is safe: roots are opaque ids + display
// labels, locations are relative paths — no absolute server paths, storage
// keys, hashes, or blob ids ever appear here.

export interface AdminImportRoot {
  rootId: string;
  label: string;
}

export interface AdminImportThrottleConfig {
  delayBetweenFilesMs: number;
  maxBytesPerSecond: number;
  maxRunMinutes: number;
  yieldEveryFiles: number;
}

export interface AdminImportRootsResponse {
  enabled: boolean;
  configured: boolean;
  roots: AdminImportRoot[];
  // Optional in the client so older mocks/responses stay valid; the backend
  // always sends it.
  throttle?: AdminImportThrottleConfig;
}

export interface AdminImportDirectoryEntry {
  name: string;
  relativePath: string;
  childDirectoryCount: number;
  fileCount: number;
}

export interface AdminImportBrowseResponse {
  rootId: string;
  relativePath: string;
  parentRelativePath: string | null;
  directories: AdminImportDirectoryEntry[];
}

export interface AdminImportUser {
  id: string;
  email: string;
  displayName: string;
  isAdmin: boolean;
  isActive: boolean;
}

export interface AdminImportFolder {
  id: string;
  name: string;
}

export interface AdminImportFoldersResponse {
  targetUserId: string;
  parentFolderId: string | null;
  folders: AdminImportFolder[];
}

export interface AdminImportPreview {
  totalFiles: number;
  totalDirectories: number;
  totalBytes: number;
  skippedSymlinks: number;
  skippedUnsupported: number;
  unreadableCount: number;
  truncated: boolean;
  warnings: string[];
}

export interface AdminImportRunResponse {
  importRunId: string;
  jobId: string | null;
  status: string;
}

// L1 aggregate metrics (any field null when not computable).
export interface AdminImportRunMetrics {
  durationMillis: number | null;
  filesPerSecond: number | null;
  bytesPerSecond: number | null;
  conflictPercent: number | null;
  skippedPercent: number | null;
  failedPercent: number | null;
  averageImportedFileBytes: number | null;
}

// L2 per-phase timing totals in ms (null when not measured / old runs).
export interface AdminImportPhaseTimings {
  readMillis: number | null;
  hashMillis: number | null;
  writeMillis: number | null;
  blobDbMillis: number | null;
  // Slice 95: minimal media detection (Metadata now = full extraction only).
  detectMillis: number | null;
  metadataMillis: number | null;
  fileItemMillis: number | null;
  thumbnailMillis: number | null;
  folderMillis: number | null;
  // Slice 95: import-item bookkeeping (page claims + terminal marks).
  itemDbMillis: number | null;
}

export interface AdminImportConflictSample {
  relativePath: string;
  reason: string;
}

export interface AdminImportRunStatus {
  importRunId: string;
  jobId: string | null;
  status: string;
  cancelRequested: boolean;
  // Slice 92: sub-phase while running ("scanning" | "importing"; else null).
  phase: string | null;
  rootId: string;
  sourceRelativePath: string;
  targetUserId: string;
  targetUserEmail: string | null;
  destinationFolderId: string | null;
  scannedFiles: number;
  // Slice 92: manifest files not yet processed.
  pendingFiles: number;
  importedFiles: number;
  skippedFiles: number;
  // deleted-content-import-skip: disjoint from skippedFiles.
  skippedPreviouslyDeletedFiles: number;
  skippedAlreadyPresentFiles: number;
  failedFiles: number;
  conflictFiles: number;
  // Subset of importedFiles re-detected on resume (not fresh ingestion).
  alreadyImportedFiles: number;
  // Files frozen unprocessed when the run was cancelled.
  cancelledFiles: number;
  importedBytes: number;
  totalBytes: number;
  totalDirectories: number;
  currentRelativePath: string | null;
  error: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
  scanCompletedAt: string | null;
  metrics: AdminImportRunMetrics;
  timings: AdminImportPhaseTimings;
  conflictSamples: AdminImportConflictSample[];
}

// Slice 92 — one safe manifest item (relative path + categories only).
export interface AdminImportItem {
  relativePath: string;
  kind: string;
  sizeBytes: number;
  status: string;
  failureCategory: string | null;
  failureMessage: string | null;
  conflictCategory: string | null;
  attempts: number;
  sourceModifiedAt: string | null;
  completedAt: string | null;
}

export interface AdminImportItemListResponse {
  importRunId: string;
  items: AdminImportItem[];
  total: number;
  page: number;
  pageSize: number;
}

export interface AdminImportEnqueueDerivativesResponse {
  importRunId: string;
  jobId: string;
  jobStatus: string;
}

export interface AdminImportRunListResponse {
  runs: AdminImportRunStatus[];
  total: number;
  limit: number;
  offset: number;
}

export interface AdminImportCancelResponse {
  cancellationRequested: boolean;
  status: string;
}

export function getImportRoots(signal?: AbortSignal): Promise<AdminImportRootsResponse> {
  return api<AdminImportRootsResponse>('/api/admin/import/roots', { signal });
}

export function browseImport(
  rootId: string,
  relativePath: string,
  signal?: AbortSignal,
): Promise<AdminImportBrowseResponse> {
  const params = new URLSearchParams({ rootId });
  if (relativePath.length > 0) params.set('relativePath', relativePath);
  return api<AdminImportBrowseResponse>(`/api/admin/import/browse?${params.toString()}`, { signal });
}

export function getImportUsers(signal?: AbortSignal): Promise<AdminImportUser[]> {
  return api<AdminImportUser[]>('/api/admin/import/users', { signal });
}

export function getDestinationFolders(
  userId: string,
  parentFolderId: string | null,
  signal?: AbortSignal,
): Promise<AdminImportFoldersResponse> {
  const params = new URLSearchParams({ userId });
  if (parentFolderId) params.set('parentFolderId', parentFolderId);
  return api<AdminImportFoldersResponse>(
    `/api/admin/import/destination-folders?${params.toString()}`,
    { signal },
  );
}

export interface AdminImportRequest {
  rootId: string;
  relativePath: string;
  targetUserId: string;
  destinationFolderId: string | null;
}

export function previewImport(
  request: AdminImportRequest,
  signal?: AbortSignal,
): Promise<AdminImportPreview> {
  return api<AdminImportPreview>('/api/admin/import/preview', { method: 'POST', json: request, signal });
}

export function runImport(
  request: AdminImportRequest,
  signal?: AbortSignal,
): Promise<AdminImportRunResponse> {
  return api<AdminImportRunResponse>('/api/admin/import/run', { method: 'POST', json: request, signal });
}

export function getImportRunStatus(
  importRunId: string,
  signal?: AbortSignal,
): Promise<AdminImportRunStatus> {
  return api<AdminImportRunStatus>(`/api/admin/import/runs/${importRunId}`, { signal });
}

export function listImportRuns(
  limit: number,
  offset: number,
  signal?: AbortSignal,
): Promise<AdminImportRunListResponse> {
  const params = new URLSearchParams({ limit: String(limit), offset: String(offset) });
  return api<AdminImportRunListResponse>(`/api/admin/import/runs?${params.toString()}`, { signal });
}

export function cancelImportRun(
  importRunId: string,
  signal?: AbortSignal,
): Promise<AdminImportCancelResponse> {
  return api<AdminImportCancelResponse>(
    `/api/admin/import/runs/${importRunId}/cancel`,
    { method: 'POST', signal },
  );
}

// Slice 92 — paginated manifest items of a run, optionally filtered by status.
export function listImportRunItems(
  importRunId: string,
  options: { status?: string; page?: number; pageSize?: number } = {},
  signal?: AbortSignal,
): Promise<AdminImportItemListResponse> {
  const params = new URLSearchParams();
  if (options.status) params.set('status', options.status);
  if (options.page) params.set('page', String(options.page));
  if (options.pageSize) params.set('pageSize', String(options.pageSize));
  const query = params.toString();
  return api<AdminImportItemListResponse>(
    `/api/admin/import/runs/${importRunId}/items${query ? `?${query}` : ''}`,
    { signal },
  );
}

// Slice 92 — enqueue the idempotent media-derivatives backfill job for a run.
export function enqueueImportDerivatives(
  importRunId: string,
  signal?: AbortSignal,
): Promise<AdminImportEnqueueDerivativesResponse> {
  return api<AdminImportEnqueueDerivativesResponse>(
    `/api/admin/import/runs/${importRunId}/enqueue-derivatives`,
    { method: 'POST', signal },
  );
}

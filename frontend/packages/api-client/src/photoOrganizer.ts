import { api } from './client';

// Client for the Phase 2 "Organize photos by date" backend. Owner-scoped; the
// responses carry logical paths + counts only (never storage internals).

export type OrganizerScope =
  | 'selected'
  | 'folder'
  | 'folder_recursive'
  | 'media_library'
  | 'all';

export type OrganizerTemplate = 'yyyy/yyyy-MM-dd' | 'yyyy/MM/dd' | 'yyyy/MM' | 'yyyy';
export type MissingDateBehavior = 'skip' | 'file_created' | 'unknown_folder';
export type OrganizerConflictPolicy = 'skip' | 'keep_both';
export type OrganizerAction = 'move' | 'already' | 'skip_missing' | 'skip_conflict';

export interface OrganizerRequest {
  scope: OrganizerScope;
  folderId?: string | null;
  fileIds?: string[];
  targetRootFolderId?: string | null;
  targetRootName?: string | null;
  template: OrganizerTemplate;
  missingDateBehavior: MissingDateBehavior;
  conflictPolicy: OrganizerConflictPolicy;
}

export interface OrganizerSourceCounts {
  userOverride: number;
  metadataOriginal: number;
  metadataFallback: number;
  fileCreatedFallback: number;
  missing: number;
}

export interface OrganizerSummary {
  candidateCount: number;
  withDateCount: number;
  missingDateCount: number;
  toMoveCount: number;
  alreadyOrganizedCount: number;
  skippedMissingCount: number;
  skippedConflictCount: number;
  foldersToCreateCount: number;
  estimatedOperations: number;
  bySource: OrganizerSourceCounts;
}

export interface OrganizerSample {
  name: string;
  currentPath: string;
  targetPath: string;
  effectiveDateTaken: string | null;
  dateTakenSource: string;
  action: OrganizerAction;
}

export interface OrganizerDryRunResponse {
  summary: OrganizerSummary;
  samples: OrganizerSample[];
}

export interface OrganizerRunResponse {
  runId: string;
  jobId: string | null;
  status: string;
}

export interface OrganizerRunStatus {
  runId: string;
  kind: string;
  status: string; // queued | running | succeeded | partial | failed | cancelled
  cancellationPending: boolean;
  template: string;
  targetRootName: string | null;
  scope: string;
  candidateCount: number;
  movedCount: number;
  alreadyOrganizedCount: number;
  skippedMissingDateCount: number;
  skippedConflictCount: number;
  failedCount: number;
  foldersCreatedCount: number;
  errorSummary: string | null;
  createdAt: string;
  startedAt: string | null;
  completedAt: string | null;
}

const BASE = '/api/photo-organizer/date-taken';

export function organizerDryRun(req: OrganizerRequest, signal?: AbortSignal): Promise<OrganizerDryRunResponse> {
  return api<OrganizerDryRunResponse>(`${BASE}/dry-run`, { method: 'POST', json: req, signal });
}

export function organizerRun(req: OrganizerRequest, signal?: AbortSignal): Promise<OrganizerRunResponse> {
  return api<OrganizerRunResponse>(`${BASE}/run`, { method: 'POST', json: req, signal });
}

export function getOrganizerRunStatus(runId: string, signal?: AbortSignal): Promise<OrganizerRunStatus> {
  return api<OrganizerRunStatus>(`${BASE}/runs/${runId}`, { signal });
}

export const ORGANIZER_TERMINAL = new Set(['succeeded', 'partial', 'failed', 'cancelled']);

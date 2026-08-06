import { api } from './client';

// Slice 90 — admin background-jobs dashboard client. Mirrors the safe DTOs in
// NubArca.Api.Jobs (AdminJobSummary / AdminJobPage). Every field is an id,
// a status/type string, a count, a timestamp, or an already-sanitized error
// string. The backend never returns PayloadJson, lock owner, storage keys,
// hashes, blob ids, paths, raw metadata, or tokens — and neither does this UI.

export interface AdminJobSummary {
  id: string;
  type: string;
  status: string;
  priority: number;
  attempts: number;
  maxAttempts: number;
  createdAt: string;
  availableAt: string;
  startedAt: string | null;
  completedAt: string | null;
  updatedAt: string;
  leaseUntil: string | null;
  heartbeatAt: string | null;
  cancellationRequested: boolean;
  progressCurrent: number | null;
  progressTotal: number | null;
  progressMessage: string | null;
  lastErrorCode: string | null;
  lastErrorMessage: string | null;
  // Scheduler v2 (safe, display-only). priorityClass is derived server-side
  // from priority; sliceNumber/yieldReason reflect cooperative slicing.
  priorityClass: string;
  sliceNumber: number;
  yieldReason: string | null;
}

export interface JobStatusCounts {
  queued: number;
  running: number;
  succeeded: number;
  failed: number;
  cancelled: number;
}

export interface AdminJobPage {
  items: AdminJobSummary[];
  page: number;
  pageSize: number;
  total: number;
  counts: JobStatusCounts;
}

export interface ListAdminJobsQuery {
  status?: string;
  type?: string;
  page?: number;
  pageSize?: number;
}

export function listAdminJobs(query: ListAdminJobsQuery = {}, signal?: AbortSignal): Promise<AdminJobPage> {
  const params = new URLSearchParams();
  if (query.status) params.set('status', query.status);
  if (query.type) params.set('type', query.type);
  if (query.page) params.set('page', String(query.page));
  if (query.pageSize) params.set('pageSize', String(query.pageSize));
  const qs = params.toString();
  return api<AdminJobPage>(`/api/admin/jobs${qs ? `?${qs}` : ''}`, { signal });
}

export function getAdminJob(id: string, signal?: AbortSignal): Promise<AdminJobSummary> {
  return api<AdminJobSummary>(`/api/admin/jobs/${id}`, { signal });
}

export function cancelAdminJob(id: string, signal?: AbortSignal): Promise<AdminJobSummary> {
  return api<AdminJobSummary>(`/api/admin/jobs/${id}/cancel`, { method: 'POST', signal });
}

// Terminal jobs cannot be cancelled (the engine + API enforce this; the UI
// hides the Cancel button for these).
export const TERMINAL_STATUSES = ['succeeded', 'failed', 'cancelled'];
export const isTerminal = (status: string) => TERMINAL_STATUSES.includes(status);

// ── Admin jobs console: catalogue of launchable commands + enqueue ──────────
// Server-driven: the console renders one form per command purely from these
// param specs, so new commands need no UI code. Safe-only — no payloads/keys.

export type AdminJobParamKind = 'bool' | 'int' | 'text' | 'guid' | 'choice';

// `choice` options: a closed set resolved server-side (the AI profiles
// registered for the command's capability), with `recommended` marking the
// configured production model.
export interface AdminJobChoice {
  value: string;
  label: string;
  recommended: boolean;
}

export interface AdminJobParamSpec {
  name: string;
  kind: AdminJobParamKind;
  required: boolean;
  min: number | null;
  max: number | null;
  defaultBool: boolean;
  defaultInt: number | null;
  danger: boolean;
  options?: AdminJobChoice[] | null;
  defaultText?: string | null;
}

export interface AdminJobCommandSpec {
  key: string;
  category: string;
  jobType: string;
  params: AdminJobParamSpec[];
  // False when the command's feature is switched off server-side; the UI marks
  // it and blocks the run rather than queueing a no-op.
  available: boolean;
  disabledReason: string | null;
}

// How many items each command would process right now, keyed by command key.
// Only commands with a meaningful, affordable count are present.
export type AdminJobPendingCounts = Record<string, number>;

export interface AdminJobCatalog {
  commands: AdminJobCommandSpec[];
}

export interface AdminJobEnqueueResult {
  jobId: string;
  jobType: string;
  // True when an identical run of this library-wide backfill was already
  // queued/running and the request collapsed onto it.
  alreadyQueued?: boolean;
}

// Submitted parameter values, keyed by param name (bool | number | string).
export type AdminJobParamValues = Record<string, boolean | number | string>;

export function getAdminJobCatalog(signal?: AbortSignal): Promise<AdminJobCatalog> {
  return api<AdminJobCatalog>('/api/admin/jobs/catalog', { signal });
}

export function getAdminJobPending(signal?: AbortSignal): Promise<AdminJobPendingCounts> {
  return api<AdminJobPendingCounts>('/api/admin/jobs/pending', { signal });
}

export function enqueueAdminJob(
  command: string,
  params: AdminJobParamValues,
  signal?: AbortSignal,
): Promise<AdminJobEnqueueResult> {
  return api<AdminJobEnqueueResult>('/api/admin/jobs/enqueue', {
    method: 'POST',
    json: { command, params },
    signal,
  });
}

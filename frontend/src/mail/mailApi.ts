import { api } from '@nubarca/api-client';
import type {
  MailArchive,
  MailFileOrigin,
  MailFolder,
  MailMessageDetail,
  MailMessagePage,
  MailRun,
  MailRunFailurePage,
  MailSourceCandidate,
  MailStatus,
} from './types';
import { filtersToApiQuery, type MailFilters } from './mailQuery';

// The owner-private MailArchive HTTP API (contract §5), over the same `api`
// helper every other client module uses: cookie auth, JSON, and ApiError for
// every non-2xx so callers handle one error type.

const enc = encodeURIComponent;

export function getMailStatus(signal?: AbortSignal): Promise<MailStatus> {
  return api<MailStatus>('/api/mail/status', { signal });
}

export async function listMailSources(signal?: AbortSignal): Promise<MailSourceCandidate[]> {
  const res = await api<{ items: MailSourceCandidate[] }>('/api/mail/sources', { signal });
  return res?.items ?? [];
}

export function startMailImport(fileItemId: string): Promise<{ archive: MailArchive; run: MailRun }> {
  return api('/api/mail/archives', { method: 'POST', json: { fileItemId } });
}

export async function listMailArchives(signal?: AbortSignal): Promise<MailArchive[]> {
  const res = await api<{ items: MailArchive[] }>('/api/mail/archives', { signal });
  return res?.items ?? [];
}

export function getMailArchive(id: string, signal?: AbortSignal): Promise<MailArchive> {
  return api<MailArchive>(`/api/mail/archives/${enc(id)}`, { signal });
}

export function resumeMailImport(archiveId: string): Promise<MailRun> {
  return api<MailRun>(`/api/mail/archives/${enc(archiveId)}/import`, { method: 'POST' });
}

export async function listMailFolders(archiveId: string, signal?: AbortSignal): Promise<MailFolder[]> {
  const res = await api<{ items: MailFolder[] }>(`/api/mail/archives/${enc(archiveId)}/folders`, { signal });
  return res?.items ?? [];
}

export function getMailRun(runId: string, signal?: AbortSignal): Promise<MailRun> {
  return api<MailRun>(`/api/mail/runs/${enc(runId)}`, { signal });
}

export function cancelMailRun(runId: string): Promise<MailRun> {
  return api<MailRun>(`/api/mail/runs/${enc(runId)}/cancel`, { method: 'POST' });
}

export function listMailRunFailures(
  runId: string, cursor: string | null, signal?: AbortSignal,
): Promise<MailRunFailurePage> {
  const qs = cursor ? `?cursor=${enc(cursor)}` : '';
  return api<MailRunFailurePage>(`/api/mail/runs/${enc(runId)}/failures${qs}`, { signal });
}

export function searchMailMessages(
  filters: MailFilters, cursor: string | null, signal?: AbortSignal, limit = 50,
): Promise<MailMessagePage> {
  const qs = filtersToApiQuery(filters, limit);
  if (cursor) qs.set('cursor', cursor);
  return api<MailMessagePage>(`/api/mail/messages?${qs.toString()}`, { signal });
}

export function getMailMessage(id: string, signal?: AbortSignal): Promise<MailMessageDetail> {
  return api<MailMessageDetail>(`/api/mail/messages/${enc(id)}`, { signal });
}

export function deleteMailMessage(id: string): Promise<void> {
  return api<void>(`/api/mail/messages/${enc(id)}`, { method: 'DELETE' });
}

export async function listMailFileOrigins(fileItemId: string, signal?: AbortSignal): Promise<MailFileOrigin[]> {
  const res = await api<{ items: MailFileOrigin[] }>(`/api/mail/file-origins/${enc(fileItemId)}`, { signal });
  return Array.isArray(res?.items) ? res.items : [];
}

// The `error` code of an API error body (`{ "error": "<code>" }`), if any.
export function errorCode(body: unknown): string | null {
  if (typeof body === 'object' && body !== null && 'error' in body) {
    const code = (body as { error?: unknown }).error;
    return typeof code === 'string' ? code : null;
  }
  return null;
}

// The `runId` a `409 import_running` carries, if any.
export function runIdOf(body: unknown): string | null {
  if (typeof body === 'object' && body !== null && 'runId' in body) {
    const id = (body as { runId?: unknown }).runId;
    return typeof id === 'string' && id.length > 0 ? id : null;
  }
  return null;
}

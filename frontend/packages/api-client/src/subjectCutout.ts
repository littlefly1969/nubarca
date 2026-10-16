import { ApiError, api } from './client';
import type { FileSummary } from './folders';

// Owner-private "copy subject": the subject of one of the caller's own photos,
// lifted off its background as a transparent PNG. Computed on request from the
// medium preview (never the original); nothing is stored unless the owner
// saves it, which files a new ordinary PNG next to the photo.

export interface SubjectCutoutAvailability {
  available: boolean;
}

// Why a cutout was not produced. `notFound` covers a foreign, missing or
// non-photo file alike; `unavailable` is the installation, never the photo.
export type SubjectCutoutFailure = 'notFound' | 'noSubject' | 'busy' | 'unavailable' | 'failed';

export class SubjectCutoutError extends Error {
  readonly reason: SubjectCutoutFailure;

  constructor(reason: SubjectCutoutFailure) {
    super(`Subject cutout failed: ${reason}`);
    this.name = 'SubjectCutoutError';
    this.reason = reason;
  }
}

export function getSubjectCutoutAvailability(signal?: AbortSignal): Promise<SubjectCutoutAvailability> {
  return api<SubjectCutoutAvailability>('/api/subject-cutout', { signal });
}

export async function createSubjectCutout(fileId: string, signal?: AbortSignal): Promise<Blob> {
  const response = await fetch(`/api/files/${fileId}/subject-cutout`, {
    method: 'POST',
    credentials: 'include',
    signal,
  });
  if (!response.ok) throw new SubjectCutoutError(await failureOf(response));
  if (!(response.headers.get('content-type') ?? '').startsWith('image/png')) {
    throw new SubjectCutoutError('failed');
  }
  // Typed explicitly: the clipboard needs an image/png Blob whatever the
  // runtime inferred.
  return new Blob([await response.arrayBuffer()], { type: 'image/png' });
}

export async function saveSubjectCutout(fileId: string): Promise<FileSummary> {
  try {
    return await api<FileSummary>(`/api/files/${fileId}/subject-cutout/save`, { method: 'POST' });
  } catch (e) {
    if (e instanceof ApiError) throw new SubjectCutoutError(failureFrom(e.status, e.body));
    throw e;
  }
}

async function failureOf(response: Response): Promise<SubjectCutoutFailure> {
  let body: unknown = null;
  try {
    body = await response.json();
  } catch {
    // Not JSON: the status alone decides.
  }
  return failureFrom(response.status, body);
}

function failureFrom(status: number, body: unknown): SubjectCutoutFailure {
  const code = typeof body === 'object' && body !== null && 'code' in body
    ? (body as { code?: unknown }).code
    : null;
  if (status === 404) return 'notFound';
  if (status === 422 || code === 'no_subject') return 'noSubject';
  if (code === 'busy') return 'busy';
  if (code === 'unavailable') return 'unavailable';
  return 'failed';
}

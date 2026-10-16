// Owner-private "copy subject". Mirrors
// frontend/packages/api-client/src/subjectCutout.ts: the server lifts the
// subject of one of the caller's own photos off its background (from the
// medium preview, never the original) and answers a transparent PNG; "save"
// files it as a new PNG next to the photo.

import { ApiError, apiGet, apiPost, apiPostForBytes } from './client.ts';

export interface SubjectCutoutAvailability {
  available: boolean;
}

export interface SavedSubjectCutout {
  id: string;
  name: string;
}

// Why no cutout came back. `notFound` covers foreign, missing and non-photo
// files alike; `unavailable` is the server, never the photo.
export type SubjectCutoutFailure = 'notFound' | 'noSubject' | 'busy' | 'unavailable' | 'failed';

// A cutout waits for the server's single inference slot and then runs a
// multi-second model: far longer than an ordinary request may take.
export const SUBJECT_CUTOUT_TIMEOUT_MS = 120_000;

export function getSubjectCutoutAvailability(signal?: AbortSignal): Promise<SubjectCutoutAvailability> {
  return apiGet<SubjectCutoutAvailability>('/api/subject-cutout', signal);
}

export async function fetchSubjectCutout(fileId: string, signal?: AbortSignal): Promise<ArrayBuffer> {
  const { bytes, contentType } = await apiPostForBytes(`/api/files/${fileId}/subject-cutout`, {
    signal,
    timeoutMs: SUBJECT_CUTOUT_TIMEOUT_MS,
  });
  if (!(contentType ?? '').startsWith('image/png')) throw new ApiError(200, 'not a PNG');
  return bytes;
}

export function saveSubjectCutout(fileId: string): Promise<SavedSubjectCutout> {
  return apiPost<SavedSubjectCutout>(`/api/files/${fileId}/subject-cutout/save`, undefined, {
    timeoutMs: SUBJECT_CUTOUT_TIMEOUT_MS,
  });
}

export function subjectCutoutFailure(error: unknown): SubjectCutoutFailure {
  if (!(error instanceof ApiError)) return 'failed';
  const body = error.body;
  const code = typeof body === 'object' && body !== null && 'code' in body
    ? (body as { code?: unknown }).code
    : null;
  if (error.status === 404) return 'notFound';
  if (error.status === 422 || code === 'no_subject') return 'noSubject';
  if (code === 'busy') return 'busy';
  if (code === 'unavailable') return 'unavailable';
  return 'failed';
}

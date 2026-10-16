import { ApiError, api } from './client';
import type { FileSummary } from './folders';

// Owner-private "copy subject": the subject of one of the caller's own photos,
// lifted off its background as a transparent PNG. Computed on request from the
// medium preview (never the original); nothing is stored unless the owner
// saves it, which files a new ordinary PNG next to the photo.

export interface SubjectCutoutAvailability {
  available: boolean;
  // The owner can tap one subject to keep only that one.
  pointSelection?: boolean;
}

// A point on the photo, normalized: (0,0) top-left, (1,1) bottom-right.
export interface SubjectPoint {
  x: number;
  y: number;
}

// Where a cutout sits in its photo, normalized like SubjectPoint.
export interface SubjectCutoutRegion {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface SubjectCutout {
  png: Blob;
  // Null when the server did not say (an older server): taps are then off.
  region: SubjectCutoutRegion | null;
}

export const SUBJECT_CUTOUT_REGION_HEADER = 'X-Subject-Cutout-Region';

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

// The whole subject, or with `point` only the subject touched there.
export async function createSubjectCutout(
  fileId: string,
  options: { point?: SubjectPoint | null; signal?: AbortSignal } = {},
): Promise<SubjectCutout> {
  const { point, signal } = options;
  const response = await fetch(`/api/files/${fileId}/subject-cutout`, {
    method: 'POST',
    credentials: 'include',
    signal,
    ...(point ? { headers: { 'content-type': 'application/json' }, body: JSON.stringify({ point }) } : {}),
  });
  if (!response.ok) throw new SubjectCutoutError(await failureOf(response));
  if (!(response.headers.get('content-type') ?? '').startsWith('image/png')) {
    throw new SubjectCutoutError('failed');
  }
  const region = parseSubjectCutoutRegion(response.headers.get(SUBJECT_CUTOUT_REGION_HEADER));
  // Typed explicitly: the clipboard needs an image/png Blob whatever the
  // runtime inferred.
  return { png: new Blob([await response.arrayBuffer()], { type: 'image/png' }), region };
}

export async function saveSubjectCutout(fileId: string, point?: SubjectPoint | null): Promise<FileSummary> {
  try {
    return await api<FileSummary>(`/api/files/${fileId}/subject-cutout/save`, {
      method: 'POST',
      ...(point ? { json: { point } } : {}),
    });
  } catch (e) {
    if (e instanceof ApiError) throw new SubjectCutoutError(failureFrom(e.status, e.body));
    throw e;
  }
}

// "x,y,width,height", each 0..1; anything else is no region.
export function parseSubjectCutoutRegion(header: string | null): SubjectCutoutRegion | null {
  if (header === null) return null;
  const parts = header.split(',').map((v) => Number(v.trim()));
  if (parts.length !== 4 || parts.some((v) => !Number.isFinite(v) || v < 0 || v > 1)) return null;
  const [x, y, width, height] = parts;
  return width > 0 && height > 0 ? { x, y, width, height } : null;
}

// A tap at (fx, fy) — fractions of the shown cutout — as a point on the photo.
export function subjectPointOnPhoto(region: SubjectCutoutRegion, fx: number, fy: number): SubjectPoint {
  const clamp = (v: number) => Math.min(1, Math.max(0, v));
  return { x: clamp(region.x + clamp(fx) * region.width), y: clamp(region.y + clamp(fy) * region.height) };
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

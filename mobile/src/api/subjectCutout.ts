// Owner-private "copy subject". Mirrors
// frontend/packages/api-client/src/subjectCutout.ts: the server lifts the
// subject of one of the caller's own photos off its background (from the
// medium preview, never the original) and answers a transparent PNG; "save"
// files it as a new PNG next to the photo.

import { ApiError, apiGet, apiPost, apiPostForBytes } from './client.ts';

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
  bytes: ArrayBuffer;
  // Null when the server did not say: taps are then off.
  region: SubjectCutoutRegion | null;
}

export const SUBJECT_CUTOUT_REGION_HEADER = 'X-Subject-Cutout-Region';

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

// The whole subject, or with `point` only the subject touched there.
export async function fetchSubjectCutout(
  fileId: string,
  options: { point?: SubjectPoint | null; signal?: AbortSignal } = {},
): Promise<SubjectCutout> {
  const { bytes, contentType, header } = await apiPostForBytes(`/api/files/${fileId}/subject-cutout`, {
    signal: options.signal,
    timeoutMs: SUBJECT_CUTOUT_TIMEOUT_MS,
    json: options.point ? { point: options.point } : undefined,
  });
  if (!(contentType ?? '').startsWith('image/png')) throw new ApiError(200, 'not a PNG');
  return { bytes, region: parseSubjectCutoutRegion(header(SUBJECT_CUTOUT_REGION_HEADER)) };
}

export function saveSubjectCutout(fileId: string, point?: SubjectPoint | null): Promise<SavedSubjectCutout> {
  return apiPost<SavedSubjectCutout>(
    `/api/files/${fileId}/subject-cutout/save`,
    point ? { point } : undefined,
    { timeoutMs: SUBJECT_CUTOUT_TIMEOUT_MS },
  );
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

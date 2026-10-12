import type { TransportResponse } from './transport';

// What went wrong with an upload, in the words the tray uses. Every server
// answer and every transport failure maps to exactly one of these, so each
// surface shows the same reason for the same cause.
export type UploadFailure =
  | 'auth'            // the session ended; nothing more can be sent
  | 'too_large'       // over the server's per-file ceiling
  | 'quota'           // the account would go over its storage quota
  | 'rejected'        // the server refused the file (type, name, path…)
  | 'name_conflict'   // a taken name the target refused to rename around
  | 'missing_target'  // the folder (or album) went away meanwhile
  | 'not_permitted'   // the album may be seen but not added to
  | 'not_media'       // an album takes only photos and videos
  | 'network'         // the connection kept failing
  | 'server'          // the server kept failing
  | 'unknown';

export type Verdict =
  | { kind: 'ok' }
  // Worth trying again by itself: a connection blip, a busy or restarting
  // server, a rate limit, or the server still finishing the same operation.
  | { kind: 'retry'; failure: 'network' | 'server'; afterMs: number | null }
  | { kind: 'fail'; failure: UploadFailure; detail: string | null; code: string | null };

function bodyField(body: unknown, field: 'error' | 'code'): string | null {
  if (typeof body !== 'object' || body === null) return null;
  const value = (body as Record<string, unknown>)[field];
  return typeof value === 'string' && value.length > 0 ? value : null;
}

export function classifyResponse(response: TransportResponse): Verdict {
  const { status, body, retryAfterMs } = response;
  const code = bodyField(body, 'code');
  const detail = bodyField(body, 'error');

  if (status >= 200 && status < 300) return { kind: 'ok' };
  if (status === 401) return { kind: 'fail', failure: 'auth', detail, code };
  if (status === 409 && code === 'upload_in_progress') {
    // The same keyed operation is still being ingested (a retry raced the
    // original). Asking again shortly returns its result.
    return { kind: 'retry', failure: 'server', afterMs: retryAfterMs ?? 2000 };
  }
  if (status === 409) return { kind: 'fail', failure: 'name_conflict', detail, code };
  if (status === 413) {
    return { kind: 'fail', failure: code === 'quota_exceeded' ? 'quota' : 'too_large', detail, code };
  }
  if (status === 404) return { kind: 'fail', failure: 'missing_target', detail, code };
  if (status === 429) return { kind: 'retry', failure: 'server', afterMs: retryAfterMs };
  if (status === 408 || status >= 500 || status === 0) {
    return { kind: 'retry', failure: 'server', afterMs: retryAfterMs };
  }
  if (status === 415 && code === 'not_media') return { kind: 'fail', failure: 'not_media', detail, code };
  if (status === 403) return { kind: 'fail', failure: 'not_permitted', detail, code };
  if (status === 400 || status === 415 || status === 422) {
    return { kind: 'fail', failure: 'rejected', detail, code };
  }
  return { kind: 'fail', failure: 'unknown', detail, code };
}

// Exponential backoff with full jitter, capped: many phones reconnecting after
// the same Wi-Fi drop must not hit the server in lockstep.
export function backoffDelay(attempt: number, baseMs: number, random: () => number, capMs = 30_000): number {
  const ceiling = Math.min(capMs, baseMs * 2 ** Math.max(0, attempt - 1));
  return Math.round(ceiling / 2 + random() * (ceiling / 2));
}

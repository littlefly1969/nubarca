import { api } from './client';

// NUBARCA-GOOGLE-CAST-01: the two authenticated calls that bracket a cast.
//
// The response's paths are ORIGIN-RELATIVE and carry the bearer secret in their
// query string. They are absolutised against the page's own secure origin at the
// moment they are handed to the Cast SDK, and they are never written anywhere
// that outlives the tab — no localStorage, no sessionStorage, no history entry,
// no telemetry. Treat the returned object as a secret for as long as you hold it.

/** How the receiver should be told to play this video. */
export type CastPlaybackMode = 'hls' | 'direct';

export interface CastGrant {
  grantId: string;
  /** ISO-8601. The server stops honouring the grant at this instant regardless. */
  expiresAt: string;
  /** Origin-relative, token-bearing. SECRET. */
  contentPath: string;
  /** Origin-relative, token-bearing. SECRET. */
  posterPath: string;
  /** `application/vnd.apple.mpegurl` for HLS, the detected video MIME otherwise. */
  contentType: string;
  streamType: 'BUFFERED';
  mode: CastPlaybackMode;
}

/**
 * The outcome of asking for a grant. `preparing` means the installation's HLS
 * ladder for this video does not exist yet — the server has enqueued the work
 * and the caller polls. It is not an error.
 */
export type CastGrantResult =
  | { status: 'ready'; grant: CastGrant }
  | { status: 'preparing'; retryAfterSeconds: number | null };

/**
 * Mints a grant for one video. Requires `cast.access`; the server independently
 * re-checks that the caller owns the file, so a UI mistake can only ever produce
 * a 403/404, never access.
 */
export async function createCastGrant(fileId: string, signal?: AbortSignal): Promise<CastGrantResult> {
  // The 202 path carries no body and a Retry-After header, so this one call
  // reaches for fetch directly instead of the JSON helper.
  const response = await fetch(`/api/cast/videos/${fileId}/grant`, {
    method: 'POST',
    credentials: 'include',
    signal,
  });

  if (response.status === 202) {
    const header = response.headers.get('retry-after');
    const parsed = header === null ? Number.NaN : Number.parseInt(header, 10);
    return {
      status: 'preparing',
      retryAfterSeconds: Number.isFinite(parsed) ? parsed : null,
    };
  }

  if (!response.ok) {
    throw new Error(`cast grant failed: ${response.status}`);
  }

  return { status: 'ready', grant: (await response.json()) as CastGrant };
}

/**
 * Withdraws a grant. Idempotent and best-effort by design: a caller invokes it
 * while tearing a session down, where throwing would be worse than failing
 * quietly — the server's expiry is the backstop that always runs.
 */
export async function revokeCastGrant(grantId: string): Promise<void> {
  try {
    await api<void>(`/api/cast/grants/${grantId}`, { method: 'DELETE' });
  } catch {
    // Deliberately swallowed. See the doc comment.
  }
}

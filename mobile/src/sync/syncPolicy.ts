// Pure sync policy: failure classification, backoff arithmetic, network
// eligibility, UI-state derivation and operation-key construction.
//
// Everything here is a pure function of its inputs so `node --test` can pin
// the exact taxonomy without a device. The engine consumes these verdicts; it
// never improvises its own.

import type {
  EngineSnapshot,
  FailureClass,
  NetworkKind,
} from './syncTypes.ts';

// ─── Failure classification ────────────────────────────────────────────────
//
// The server speaks plain REST on POST /api/files (ApiError carries status).
// The taxonomy mirrors NubArca's semantics:
//   401             → the SESSION died; auth recovery owns it, sync pauses.
//   403             → refused deliberately; retrying would only spin.
//   408 / 429 / 5xx → transient; bounded retries with backoff apply.
//   413 / 415       → permanent user-visible failures (size / media type).
//   other known 4xx → permanent unless the existing API says otherwise —
//                     notably 409: with replay-safety in place a 409 is a
//                     genuine name conflict, not an ambiguous lost retry.

const RETRYABLE_STATUSES = new Set([408, 429]);
const PERMANENT_STATUSES = new Set([
  400, 402, 403, 404, 405, 409, 410, 411, 412, 413, 414, 415, 416, 418, 422,
]);

export interface ClassifiedFailure {
  cls: FailureClass;
}

export function classifyHttpFailure(status: number): ClassifiedFailure {
  if (status === 401) return { cls: 'auth' };
  if (RETRYABLE_STATUSES.has(status) || status >= 500) {
    return { cls: 'retryable-status' };
  }
  if (PERMANENT_STATUSES.has(status)) return { cls: 'permanent-status' };
  // Unknown statuses fail conservative-but-calm: not permanent (the server
  // may grow new meanings), retried within the bounded per-item budget.
  return { cls: 'retryable-status' };
}

/**
 * Parse a Retry-After header value into an absolute epoch-ms deadline,
 * capped at `nowMs + maxDelayMs`. Accepts delta-seconds and HTTP-date forms;
 * anything invalid yields null (caller falls back to ordinary backoff).
 */
export function parseRetryAfterHeader(
  value: string | null | undefined,
  nowMs: number,
  maxDelayMs: number,
): number | null {
  if (!value) return null;
  const trimmed = value.trim();
  if (/^\d+$/.test(trimmed)) {
    const seconds = Number.parseInt(trimmed, 10);
    if (!Number.isFinite(seconds) || seconds < 0) return null;
    return Math.min(nowMs + seconds * 1000, nowMs + maxDelayMs);
  }
  const date = Date.parse(trimmed);
  if (Number.isNaN(date)) return null;
  const clamped = Math.min(date, nowMs + maxDelayMs);
  return clamped > nowMs ? clamped : null;
}

// ─── Backoff ───────────────────────────────────────────────────────────────

/**
 * Exponential backoff with FULL jitter: uniform in [0, min(cap, base·2^n)].
 * Full jitter spreads a large failed cohort over the whole window instead of
 * having every device fire simultaneously at the same multiples.
 */
export function backoffDelayMs(
  attempt: number,
  config: { baseMs: number; maxMs: number },
  random: () => number,
): number {
  const safeAttempt = Math.max(1, Math.floor(attempt));
  const exponential = Math.min(
    config.maxMs,
    config.baseMs * 2 ** (safeAttempt - 1),
  );
  return Math.floor(random() * exponential);
}

// ─── Network eligibility ───────────────────────────────────────────────────

export function isUploadAllowed(wifiOnly: boolean, kind: NetworkKind): boolean {
  if (kind === 'wifi') return true;
  if (kind === 'cellular') return !wifiOnly;
  return false;
}

// ─── UI-state derivation ───────────────────────────────────────────────────

export type SyncUiStatus =
  | 'off'
  | 'permission-required'
  | 'scanning'
  | 'pending'
  | 'uploading'
  | 'paused'
  | 'waiting-wifi'
  | 'up-to-date'
  | 'attention'
  | 'auth-required';

/**
 * ONE user-facing status, derived. Precedence follows what a user must act
 * on: off → permission → paused/auth → network wait → live work → problems.
 */
export function deriveUiStatus(snapshot: EngineSnapshot): SyncUiStatus {
  const { settings, phase, permission } = snapshot;
  if (!settings.enabled) return 'off';
  if (permission === 'denied' || permission === 'undetermined') {
    return 'permission-required';
  }
  if (phase === 'paused') return snapshot.authRequired ? 'auth-required' : 'paused';
  if (snapshot.uploadingCount > 0) return 'uploading';
  if (
    phase === 'waiting-network' &&
    snapshot.pendingCount + snapshot.retryableCount > 0
  ) {
    return settings.wifiOnly ? 'waiting-wifi' : 'pending';
  }
  if (phase === 'discovering') return 'scanning';
  if (snapshot.permanentCount > 0) return 'attention';
  if (snapshot.pendingCount + snapshot.retryableCount > 0) return 'pending';
  return 'up-to-date';
}

// ─── Operation keys ────────────────────────────────────────────────────────

/**
 * Build the opaque OPERATION identity for one logical sync of one asset.
 *
 * This is deliberately NOT a content hash: blob identity belongs to the
 * server's SHA-256 model. The key answers only "which logical ingestion is
 * this?" — stable across ambiguous retries of the SAME logical upload, and
 * different when the asset revision actually moved (that genuinely is another
 * logical upload).
 *
 * Format: `sv1.<16 hex>` — two salted FNV-1a rounds (64 effective bits),
 * inside the server's Idempotency-Key grammar [A-Za-z0-9._:-]{8,128},
 * carrying no readable inventory data.
 */
export function buildOperationKey(
  accountId: string,
  assetId: string,
  revision: number,
): string {
  const payload = `${accountId} sv1 ${assetId} ${revision}`;
  const a = fnv1a(payload, 0x811c9dc5);
  const b = fnv1a(payload, 0x01000193);
  const hex = (n: number) => (n >>> 0).toString(16).padStart(8, '0');
  return `sv1.${hex(a)}${hex(b)}`;
}

function fnv1a(input: string, seed: number): number {
  let hash = seed >>> 0;
  for (let i = 0; i < input.length; i++) {
    hash ^= input.charCodeAt(i);
    // Math.imul keeps the round in 32-bit space.
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return hash >>> 0;
}

/** True when the value fits the server's Idempotency-Key grammar. */
export function isValidOperationKey(key: string): boolean {
  return /^[A-Za-z0-9._:-]{8,128}$/.test(key);
}

// ─── MIME hint mapping ─────────────────────────────────────────────────────

/**
 * Best-effort MIME hint from the platform filename. The server remains
 * authoritative: it normalizes MIME and re-detects image/video facts itself.
 */
export function mimeFromFilename(filename: string | null): string {
  if (!filename) return 'application/octet-stream';
  const dot = filename.lastIndexOf('.');
  if (dot < 0 || dot === filename.length - 1) return 'application/octet-stream';
  const ext = filename.slice(dot + 1).toLowerCase();
  const table: Record<string, string> = {
    jpg: 'image/jpeg',
    jpeg: 'image/jpeg',
    png: 'image/png',
    gif: 'image/gif',
    webp: 'image/webp',
    heic: 'image/heic',
    heif: 'image/heif',
    bmp: 'image/bmp',
    dng: 'image/x-adobe-dng',
    mp4: 'video/mp4',
    mov: 'video/quicktime',
    m4v: 'video/x-m4v',
    mkv: 'video/x-matroska',
    webm: 'video/webm',
    avi: 'video/x-msvideo',
    '3gp': 'video/3gpp',
  };
  return table[ext] ?? 'application/octet-stream';
}



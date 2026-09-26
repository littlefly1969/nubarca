// The party GAME display grant's lifecycle — a PORT of the pure half of
// tv/src/lib/partyDisplayGrant.ts. The browser renders the canonical stage in
// its own document rather than in a WebView, so the bridge half (stage URL,
// renderer messages) has no counterpart here; the rules for WHEN to mint, retry
// and renew are the same, and `nativeParity.test.ts` holds them together.

/** Why a mint did not produce a grant — the three answers that lead somewhere different. */
export type MintFailure =
  /** 401: the TELEVISION's session is gone. Pairing, not retrying. */
  | 'session-invalid'
  /** 404: paired, but not assigned to a showable party right now. */
  | 'not-assigned'
  /** Network, 5xx, 429, anything else. */
  | 'transient';

/** `status` is the HTTP status, or null when no response arrived at all. */
export function classifyMintFailure(status: number | null): MintFailure {
  if (status === 401) return 'session-invalid';
  if (status === 404) return 'not-assigned';
  return 'transient';
}

export const MINT_RETRY_BASE_MS = 2_000;
export const MINT_RETRY_MAX_MS = 30_000;
export const NOT_ASSIGNED_RETRY_BASE_MS = 5_000;

export function mintRetryDelayMs(failure: Exclude<MintFailure, 'session-invalid'>, attempt: number): number {
  const base = failure === 'not-assigned' ? NOT_ASSIGNED_RETRY_BASE_MS : MINT_RETRY_BASE_MS;
  const steps = Math.max(0, Math.min(attempt, 10));
  return Math.min(MINT_RETRY_MAX_MS, base * 2 ** steps);
}

export const RENEW_BEFORE_EXPIRY_MS = 10 * 60_000;
export const MIN_RENEW_DELAY_MS = 30_000;

export interface GrantLifetime {
  readonly expiresAt: string;
  readonly expiresInSeconds?: number | null;
}

/**
 * When to replace a freshly minted grant: a margin before it lapses, measured
 * by the SERVER's duration rather than this machine's clock, and never sooner
 * than MIN_RENEW_DELAY_MS.
 */
export function renewDelayMs(grant: GrantLifetime, now: number): number {
  const lifetimeMs = typeof grant.expiresInSeconds === 'number' && grant.expiresInSeconds > 0
    ? grant.expiresInSeconds * 1_000
    : Date.parse(grant.expiresAt) - now;
  if (!Number.isFinite(lifetimeMs)) return MIN_RENEW_DELAY_MS;
  const margin = Math.min(RENEW_BEFORE_EXPIRY_MS, lifetimeMs / 4);
  return Math.max(MIN_RENEW_DELAY_MS, Math.round(lifetimeMs - margin));
}

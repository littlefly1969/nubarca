// The display grant's lifecycle on the television. Pure, node-testable.
//
// The grant is the WebView's only credential and the shell owns it: it mints
// one when the game takes the screen, renews it BEFORE it expires, and mints
// again whenever the page reports that the grant stopped working. Every
// decision about WHEN is here, as arithmetic, because a timer inside a
// component is the one place nobody can test.

/** Why a mint did not produce a grant — the three answers that lead somewhere different. */
export type MintFailure =
  /** 401: the TELEVISION's session is gone. Pairing, not retrying. */
  | 'session-invalid'
  /** 404: paired, but not assigned to a showable party right now. Fail closed and wait. */
  | 'not-assigned'
  /** Network, 5xx, 429, anything else: the same request may well work shortly. */
  | 'transient';

/** `status` is the HTTP status, or null when no response arrived at all. */
export function classifyMintFailure(status: number | null): MintFailure {
  if (status === 401) return 'session-invalid';
  if (status === 404) return 'not-assigned';
  return 'transient';
}

export const MINT_RETRY_BASE_MS = 2_000;
export const MINT_RETRY_MAX_MS = 30_000;
/**
 * A 404 is the server's considered answer, not a hiccup: the control plane will
 * move the television within a poll. Retrying it is only a safety net for the
 * instant between an assignment arriving and the party becoming showable.
 */
export const NOT_ASSIGNED_RETRY_BASE_MS = 5_000;

/**
 * How long to wait before minting again after `attempt` consecutive failures.
 *
 * Exponential and CAPPED: a server that is down for an hour is asked twice a
 * minute, never in a tight loop, and a transient blip costs two seconds.
 */
export function mintRetryDelayMs(failure: Exclude<MintFailure, 'session-invalid'>, attempt: number): number {
  const base = failure === 'not-assigned' ? NOT_ASSIGNED_RETRY_BASE_MS : MINT_RETRY_BASE_MS;
  const steps = Math.max(0, Math.min(attempt, 10));
  return Math.min(MINT_RETRY_MAX_MS, base * 2 ** steps);
}

/** Renew this long before the grant would lapse, at most. */
export const RENEW_BEFORE_EXPIRY_MS = 10 * 60_000;
/** Never renew sooner than this after a mint, whatever the grant says. */
export const MIN_RENEW_DELAY_MS = 30_000;

export interface GrantLifetime {
  /** Server wall-clock expiry. Only trusted when the duration below is absent. */
  readonly expiresAt: string;
  /** The same lifetime as a server-computed DURATION. */
  readonly expiresInSeconds?: number | null;
}

/**
 * When to replace a freshly minted grant.
 *
 * The lifetime is taken from `expiresInSeconds` — a duration, measured by the
 * server — rather than by subtracting the television's own clock from
 * `expiresAt`. A Fire TV whose clock is an hour out would otherwise either
 * renew in a loop or let the grant lapse in the middle of a round. Only a server
 * that predates the field falls back to the device clock.
 *
 * Renewal happens a margin before expiry (a quarter of the lifetime, capped at
 * ten minutes), and never sooner than MIN_RENEW_DELAY_MS so a nonsensical
 * lifetime cannot become a mint loop. A grant that lapses anyway is still
 * recovered: the page reports the 401 and the shell mints again.
 */
export function renewDelayMs(grant: GrantLifetime, now: number): number {
  const lifetimeMs = typeof grant.expiresInSeconds === 'number' && grant.expiresInSeconds > 0
    ? grant.expiresInSeconds * 1_000
    : Date.parse(grant.expiresAt) - now;
  if (!Number.isFinite(lifetimeMs)) return MIN_RENEW_DELAY_MS;
  const margin = Math.min(RENEW_BEFORE_EXPIRY_MS, lifetimeMs / 4);
  return Math.max(MIN_RENEW_DELAY_MS, Math.round(lifetimeMs - margin));
}

/**
 * The URL the WebView is given. The grant travels in the FRAGMENT: a browser
 * never sends it to a server, so it cannot reach an access log or a Referer
 * header, and the page strips it from history on its first render.
 */
export function stageUrl(baseUrl: string, grant: string): string {
  return `${baseUrl}/party-display/stage#grant=${encodeURIComponent(grant)}`;
}

/**
 * What the canonical renderer may tell the shell, and nothing else.
 *
 * `heartbeat` — the page's JavaScript is alive. It says nothing about the
 *   grant or the server: a page can be alive and unauthorised.
 * `ready` — the page has rendered its first valid snapshot, so it is safe to
 *   take the native cover down.
 * `auth-failed` — the display grant was refused. Distinct from silence on
 *   purpose: the renderer is healthy, the CAPABILITY is not.
 * `presentation` — whether a live scene of the party is on screen, the only
 *   thing the keep-awake policy needs from the page. Coarse by design: the
 *   shell never learns which scene.
 */
export type RendererMessage =
  | { kind: 'heartbeat'; protocol: number }
  | { kind: 'ready' }
  | { kind: 'auth-failed' }
  | { kind: 'presentation'; active: boolean };

/** The bridge protocol this shell speaks. A page announcing less is a legacy page. */
export const RENDERER_PROTOCOL = 2;

/**
 * Parse one bridge message. Anything malformed is null — and null is simply
 * silence, which the watchdog already knows how to treat.
 */
export function parseRendererMessage(raw: string): RendererMessage | null {
  let message: unknown;
  try {
    message = JSON.parse(raw);
  } catch {
    return null;
  }
  if (typeof message !== 'object' || message === null) return null;
  const { type, protocol, active } = message as { type?: unknown; protocol?: unknown; active?: unknown };
  switch (type) {
    case 'display-heartbeat':
      return { kind: 'heartbeat', protocol: typeof protocol === 'number' ? protocol : 1 };
    case 'display-ready':
      return { kind: 'ready' };
    case 'display-auth-failed':
      return { kind: 'auth-failed' };
    case 'display-presentation':
      return typeof active === 'boolean' ? { kind: 'presentation', active } : null;
    default:
      return null;
  }
}

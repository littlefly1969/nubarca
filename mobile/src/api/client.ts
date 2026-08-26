// NubArca mobile API client core.
//
// Session model: the NubArca owner session is an HttpOnly cookie (no JWT, no
// bearer). RN fetch keeps no cookie jar, so the ONE `NubArca.Auth` pair is
// captured out of Set-Cookie responses by OwnerSessionCookieStore and re-sent
// via the manual Cookie header on every request.
//
// Request correctness rules:
//   * every request accepts an AbortSignal and enforces a timeout — no
//     operation can hang forever;
//   * a response may refresh the session cookie ONLY while its request's
//     session generation is still current — a stale response from a
//     logged-out or switched account can never mutate the live cookie;
//   * 401 on an authenticated request is normalized globally: the registered
//     unauthorized handler runs exactly once per invalid session (the login
//     request itself opts out — a wrong password is not a dead session);
//   * error messages carry method, path and status only. Cookie values,
//     response bodies with secrets, and headers never appear in errors.

import { sessionCookieSource } from './sessionAccess.ts';

export class ApiError extends Error {
  status: number;
  body: unknown;
  /**
   * Parsed Retry-After hint (epoch ms) when the failing response carried a
   * valid one. Populated only for retryable statuses (429/503-style); sync's
   * retry policy honors it, everything else ignores it.
   */
  retryAfterAtMs: number | null;

  constructor(
    status: number,
    message: string,
    body: unknown = null,
    retryAfterAtMs: number | null = null,
  ) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
    this.retryAfterAtMs = retryAfterAtMs;
  }
}

type UnauthorizedHandler = () => void;
let unauthorizedHandler: UnauthorizedHandler | null = null;

// Registered once by the auth provider. Invoked when an AUTHENTICATED request
// comes back 401, meaning the session is no longer valid.
export function setUnauthorizedHandler(handler: UnauthorizedHandler | null): void {
  unauthorizedHandler = handler;
}

let baseUrl = '';

export function configureBaseUrl(url: string): void {
  baseUrl = url.replace(/\/$/, '');
}

export function getBaseUrl(): string {
  return baseUrl;
}

export const DEFAULT_TIMEOUT_MS = 20_000;

export interface RequestOptions {
  json?: unknown;
  /**
   * Multipart body escape hatch for FILE-BACKED uploads: parts reference
   * native file URIs ({ uri, name, type }) so original bytes stream through
   * the native networking stack and never enter the JS heap. When present,
   * `json` must be absent and no content-type is set (RN supplies the
   * multipart boundary).
   */
  form?: FormData;
  /** Extra headers for contract-relevant metadata (e.g. Idempotency-Key). */
  headers?: Record<string, string>;
  signal?: AbortSignal;
  timeoutMs?: number;
  // True for endpoints whose 401 means "rejected credentials" rather than
  // "the session died" (login). Never set it on ordinary authenticated calls.
  allow401?: boolean;
  // Explicit Cookie header for the ONE case where the seam must NOT be read:
  // the best-effort logout notification, sent AFTER the local teardown has
  // already wiped the jar with a snapshot of the pre-teardown cookie.
  cookieOverride?: string;
}

// Combine the caller's signal and the timeout into one controller so either
// aborts the fetch.
function linkSignals(
  signal: AbortSignal | undefined,
  timeoutMs: number,
): { controller: AbortController; cleanup: () => void } {
  const controller = new AbortController();
  const timer = setTimeout(() => {
    controller.abort(new Error(`Request timed out after ${timeoutMs}ms`));
  }, timeoutMs);
  const onAbort = () => controller.abort(signal?.reason);
  if (signal) {
    if (signal.aborted) {
      onAbort();
    } else {
      signal.addEventListener('abort', onAbort, { once: true });
    }
  }
  return {
    controller,
    cleanup: () => {
      clearTimeout(timer);
      signal?.removeEventListener('abort', onAbort);
    },
  };
}

async function request<T>(
  method: string,
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  const { json, form, signal, allow401 = false, cookieOverride, headers: extraHeaders } = options;
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const linked = linkSignals(signal, timeoutMs);

  const headers: Record<string, string> = {};
  if (json !== undefined) headers['content-type'] = 'application/json';
  if (extraHeaders) {
    for (const [name, value] of Object.entries(extraHeaders)) {
      headers[name.toLowerCase()] = value;
    }
  }
  // Snapshot ONCE at request start: this request belongs to THIS session
  // generation even if a logout or account switch happens while it flies.
  const session = sessionCookieSource().snapshot();
  const cookie = cookieOverride ?? session.cookie;
  if (cookie) headers['cookie'] = cookie;

  try {
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      headers,
      body: json !== undefined ? JSON.stringify(json) : form ?? undefined,
      signal: linked.controller.signal,
      // Belt-and-suspenders hint; the manual jar above is the real mechanism.
      credentials: 'include',
    });

    // Accept a refreshed/rotated session cookie ONLY while this response
    // still belongs to the session its request started under (generation
    // unchanged). The cookieOverride case is the best-effort logout
    // notification riding a deliberately DEAD session: its response feeds
    // nothing, ever.
    if (cookieOverride === undefined) {
      void sessionCookieSource().captureIfCurrent(
        res.headers.get('set-cookie'),
        session.generation,
      );
    }

    if (res.status === 204) return undefined as T;

    if (!res.ok) {
      // Retry-After is read BEFORE body parsing so sync can honor it even
      // when the error body is opaque. Capped by the caller's policy later.
      let retryAfterAtMs: number | null = null;
      const retryAfterRaw = res.headers.get('retry-after');
      if (retryAfterRaw && (res.status === 429 || res.status === 503)) {
        const seconds = Number.parseInt(retryAfterRaw.trim(), 10);
        if (/^\d+$/.test(retryAfterRaw.trim()) && Number.isFinite(seconds) && seconds >= 0) {
          retryAfterAtMs = Date.now() + seconds * 1000;
        } else {
          const httpDate = Date.parse(retryAfterRaw);
          if (!Number.isNaN(httpDate)) retryAfterAtMs = httpDate;
        }
      }

      const text = await res.text();
      let parsed: unknown = null;
      try {
        parsed = text ? (JSON.parse(text) as unknown) : null;
      } catch {
        parsed = text;
      }

      if (res.status === 401 && !allow401 && unauthorizedHandler) {
        unauthorizedHandler();
      }
      throw new ApiError(
        res.status,
        `${method} ${path} → ${res.status}`,
        parsed,
        retryAfterAtMs,
      );
    }

    const text = await res.text();
    let parsed: unknown = null;
    try {
      parsed = text ? (JSON.parse(text) as unknown) : null;
    } catch {
      parsed = text;
    }
    return parsed as T;
  } finally {
    linked.cleanup();
  }
}

export function apiGet<T>(path: string, signal?: AbortSignal): Promise<T> {
  return request<T>('GET', path, { signal });
}

// Escape hatch for callers needing full options on non-standard verbs.
export function apiRequest<T>(
  method: string,
  path: string,
  options: RequestOptions = {},
): Promise<T> {
  return request<T>(method, path, options);
}

export function apiPost<T>(
  path: string,
  json?: unknown,
  options: Omit<RequestOptions, 'json'> = {},
): Promise<T> {
  return request<T>('POST', path, { ...options, json });
}

export function apiPatch<T>(
  path: string,
  json?: unknown,
  options: Omit<RequestOptions, 'json'> = {},
): Promise<T> {
  return request<T>('PATCH', path, { ...options, json });
}

// DELETE may carry a JSON body (bulk album removal does).
export function apiDelete<T>(
  path: string,
  json?: unknown,
  options: Omit<RequestOptions, 'json'> = {},
): Promise<T> {
  return request<T>('DELETE', path, { ...options, json });
}

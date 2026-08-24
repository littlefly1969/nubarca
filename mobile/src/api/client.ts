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
//   * 401 on an authenticated request is normalized globally: the registered
//     unauthorized handler runs exactly once per invalid session (the login
//     request itself opts out — a wrong password is not a dead session);
//   * error messages carry method, path and status only. Cookie values,
//     response bodies with secrets, and headers never appear in errors.

import { sessionCookieSource } from './sessionAccess.ts';

export class ApiError extends Error {
  status: number;
  body: unknown;

  constructor(status: number, message: string, body: unknown = null) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.body = body;
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
  const { json, signal, allow401 = false, cookieOverride } = options;
  const timeoutMs = options.timeoutMs ?? DEFAULT_TIMEOUT_MS;
  const linked = linkSignals(signal, timeoutMs);

  const headers: Record<string, string> = {};
  if (json !== undefined) headers['content-type'] = 'application/json';
  const cookie = cookieOverride ?? sessionCookieSource().current;
  if (cookie) headers['cookie'] = cookie;

  try {
    const res = await fetch(`${baseUrl}${path}`, {
      method,
      headers,
      body: json !== undefined ? JSON.stringify(json) : undefined,
      signal: linked.controller.signal,
      // Belt-and-suspenders hint; the manual jar above is the real mechanism.
      credentials: 'include',
    });

    // Capture a refreshed/rotated session cookie if the response carried one.
    void sessionCookieSource().capture(res.headers.get('set-cookie'));

    if (res.status === 204) return undefined as T;

    const text = await res.text();
    let parsed: unknown = null;
    try {
      parsed = text ? (JSON.parse(text) as unknown) : null;
    } catch {
      parsed = text;
    }

    if (!res.ok) {
      if (res.status === 401 && !allow401 && unauthorizedHandler) {
        unauthorizedHandler();
      }
      throw new ApiError(res.status, `${method} ${path} → ${res.status}`, parsed);
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

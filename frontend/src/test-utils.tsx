// Small helpers shared across vitest test files. Kept in `src/` (not under
// a separate `tests/` directory) so it lives inside the same tsconfig include
// as the app code — no extra TS project plumbing needed.
//
// Two main exports:
//   * `installFetchMock(handlers)` — replaces `globalThis.fetch` with a
//     handler-table-backed stub. Tests register `${METHOD} ${path}` keys (or
//     the wildcard `* ${path}` for any method) and return real Response
//     objects. Missing routes throw with a clear message so test mistakes
//     surface loudly instead of as silent hangs.
//   * `AuthedWrapper` / `AnonWrapper` — render-time context providers that
//     short-circuit the real `AuthProvider`'s `/api/auth/me` probe, so each
//     test can render a component in isolation against a known auth state.
import type { ReactNode } from 'react';
import { act } from 'react';
import { vi } from 'vitest';
import type { FileMetadata } from '@nubarca/api-client';
import { AuthContext, type AuthContextValue, type AuthState } from './auth/AuthContext';
import { I18nProvider } from './i18n';

// Slice 80: fire every active (mocked) IntersectionObserver as if its observed
// element scrolled into view. Wrapped in act() because it drives React state
// updates (the gallery's next-page fetch). See vitest.setup.ts for the mock.
export function triggerIntersection(): void {
  act(() => {
    (globalThis as unknown as { __fireIntersection?: (v?: boolean) => void }).__fireIntersection?.(true);
  });
}

export interface MockRequest {
  url: string;
  method: string;
  init?: RequestInit;
  body: string | null;
}

export type MockHandler = (req: MockRequest) => Response | Promise<Response>;

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

export function emptyResponse(status = 204): Response {
  return new Response(null, { status });
}

export function errorResponse(status: number, body: unknown = null): Response {
  return new Response(body === null ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

// Complete metadata contract for tests that open the shared viewer. Keeping
// this typed prevents a bare `{}` response from making a viewer test look green
// while React reports an asynchronous render exception after the assertion.
export function fileMetadata(
  id: string,
  name: string,
  mimeType = 'video/mp4',
): FileMetadata {
  return {
    id,
    name,
    mimeType,
    sizeBytes: 1_024,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    blob: {
      mediaCategory: mimeType.startsWith('video/') ? 'video' : 'image',
      detectedContentType: mimeType,
      detectedFormat: mimeType.startsWith('video/') ? 'mp4' : 'jpeg',
      width: 1920,
      height: 1080,
      pixelCount: 2_073_600,
      thumbnailStatus: 'ready',
      extractionStatus: 'ready',
      embedded: null,
      video: null,
    },
    user: {
      title: null,
      description: null,
      tags: [],
      rating: null,
      favorite: false,
      dateTakenOverride: null,
      locationOverride: null,
    },
    effective: {
      displayName: name,
      dateTaken: '2026-01-01T00:00:00Z',
      dateTakenSource: 'uploaded',
      location: null,
    },
  };
}

export interface FetchSpyEntry {
  url: string;
  method: string;
  body: string | null;
  init?: RequestInit;
}

export interface InstalledFetchMock {
  calls: FetchSpyEntry[];
}

// Installs a fetch stub on globalThis backed by the handler table. Returns a
// `calls` array so a test can assert which URLs were hit (and with which
// bodies) without having to reach into `vi.mocked(fetch)` repeatedly.
export function installFetchMock(
  handlers: Record<string, MockHandler>,
): InstalledFetchMock {
  const calls: FetchSpyEntry[] = [];
  const fetchImpl = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url =
      typeof input === 'string'
        ? input
        : input instanceof URL
          ? input.toString()
          : input.url;
    const method = (init?.method ?? 'GET').toUpperCase();
    const body =
      init?.body === undefined || init?.body === null
        ? null
        : typeof init.body === 'string'
          ? init.body
          : init.body instanceof FormData
            ? '[FormData]'
            : String(init.body);
    calls.push({ url, method, body, init });

    const exactKey = `${method} ${url}`;
    const wildcardKey = `* ${url}`;
    const pathOnly = url.split('?')[0];
    const pathExactKey = `${method} ${pathOnly}`;
    const pathWildcardKey = `* ${pathOnly}`;

    const handler =
      handlers[exactKey] ??
      handlers[wildcardKey] ??
      handlers[pathExactKey] ??
      handlers[pathWildcardKey];
    if (handler === undefined) {
      throw new Error(`installFetchMock: no handler for ${method} ${url}`);
    }
    return handler({ url, method, init, body });
  });

  vi.stubGlobal('fetch', fetchImpl);
  return { calls };
}

// Lightweight auth context wrapper. Pass `state` to drive the consumer
// without spinning up the real provider's mount-time fetch effect.
export function makeAuthValue(
  state: AuthState,
  overrides: Partial<AuthContextValue> = {},
): AuthContextValue {
  return {
    state,
    login: overrides.login ?? (async () => {}),
    logout: overrides.logout ?? (async () => {}),
    invalidateAuth: overrides.invalidateAuth ?? (() => {}),
    updateUser: overrides.updateUser ?? (() => {}),
  };
}

export function AuthedWrapper({
  children,
  value,
  isAdmin = false,
}: {
  children: ReactNode;
  value?: Partial<AuthContextValue>;
  // Slice 47: tests can opt into an admin principal by passing isAdmin.
  isAdmin?: boolean;
}) {
  const auth = makeAuthValue(
    {
      status: 'authed',
      user: {
        id: 'user-1',
        email: 'dev@nubarca.local',
        displayName: 'Dev User',
        isAdmin,
        language: 'it',
      },
    },
    value,
  );
  return (
    <I18nProvider>
      <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>
    </I18nProvider>
  );
}

export function AnonWrapper({
  children,
  value,
}: {
  children: ReactNode;
  value?: Partial<AuthContextValue>;
}) {
  const auth = makeAuthValue({ status: 'anon' }, value);
  return (
    <I18nProvider>
      <AuthContext.Provider value={auth}>{children}</AuthContext.Provider>
    </I18nProvider>
  );
}

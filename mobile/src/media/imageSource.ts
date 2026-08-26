// Authenticated media sources.
//
// RN's <Image> and expo-video both accept a source of the shape
// { uri, headers }. The NubArca owner session is a Cookie header — never a
// URL query token, never a bearer. These builders centralize that rule so no
// screen ever constructs an authenticated media URL by hand:
//
//   * the exact current `NubArca.Auth` pair rides in the Cookie header;
//   * without a session cookie there is NO source (null) — an unauthenticated
//     request is never issued and cannot leak bytes into a native cache;
//   * only derivative endpoints (thumbnail / preview / poster / Range video)
//     are ever addressed; originals are not part of this slice.

import { getBaseUrl } from '../api/client.ts';
import { sessionCookieSource } from '../api/sessionAccess.ts';

export interface AuthenticatedSource {
  uri: string;
  headers: { cookie: string };
}

// Pure builder (unit-tested): exact cookie header or nothing.
export function buildAuthenticatedSource(
  baseUrl: string,
  cookie: string | null,
  path: string,
): AuthenticatedSource | null {
  if (!cookie || cookie.length === 0) return null;
  return {
    uri: `${baseUrl}${path}`,
    headers: { cookie },
  };
}

// Snapshot the CURRENT session into a source. Returns null while signed out.
export function authenticatedSource(path: string): AuthenticatedSource | null {
  return buildAuthenticatedSource(
    getBaseUrl(),
    sessionCookieSource().current,
    path,
  );
}

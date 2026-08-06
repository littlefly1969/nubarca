// NubArca mobile API client.
//
// Cookie handling: React Native's fetch does not maintain a browser-style
// cookie jar. We capture Set-Cookie from the login response and forward it
// manually via the Cookie request header.
//
// The backend cookie is HttpOnly — neither JS nor RN code can read it from a
// DOM. In RN we read it from the response headers (RN does expose Set-Cookie,
// unlike browsers where it is blocked). credentials:'include' is set as a
// belt-and-suspenders hint, but the manual jar is the reliable path.
//
// Persistence: the captured cookie + base URL are stored in expo-secure-store
// (Android Keystore / iOS Keychain backed) so the session survives an app
// restart. This is still a SESSION COOKIE, not a token — no JWT, no bearer.

import * as SecureStore from 'expo-secure-store';
import { clearImageCache } from './imageLoader';

const COOKIE_KEY = 'nc_session_cookie';
const BASEURL_KEY = 'nc_base_url';

let _baseUrl = '';
let _cookieJar: string | null = null;

export function configure(baseUrl: string): void {
  _baseUrl = baseUrl.replace(/\/$/, '');
}

export function getBaseUrl(): string {
  return _baseUrl;
}

export function hasCookie(): boolean {
  return _cookieJar !== null;
}

export function clearCookies(): void {
  _cookieJar = null;
}

export function cookieStatus(): { captured: boolean; preview: string } {
  if (!_cookieJar) return { captured: false, preview: '(none)' };
  const preview = _cookieJar.length > 24 ? `${_cookieJar.slice(0, 24)}…` : _cookieJar;
  return { captured: true, preview };
}

// ---------------------------------------------------------------------------
// Secure persistence (cookie + base URL).
// ---------------------------------------------------------------------------

// Save the current session (cookie + base URL) to secure storage. Call after a
// successful login. No-op if there is no cookie to persist.
export async function persistSession(): Promise<void> {
  if (_cookieJar === null) return;
  await SecureStore.setItemAsync(COOKIE_KEY, _cookieJar);
  await SecureStore.setItemAsync(BASEURL_KEY, _baseUrl);
}

// Restore a previously persisted session into memory. Returns true if a cookie
// was restored (the caller should then validate it via /api/auth/me). Restoring
// also re-applies the saved base URL so subsequent requests target the right
// server.
export async function restoreSession(): Promise<boolean> {
  const [cookie, baseUrl] = await Promise.all([
    SecureStore.getItemAsync(COOKIE_KEY),
    SecureStore.getItemAsync(BASEURL_KEY),
  ]);
  if (cookie === null || baseUrl === null) return false;
  _cookieJar = cookie;
  _baseUrl = baseUrl.replace(/\/$/, '');
  return true;
}

// Clear the in-memory cookie AND the persisted cookie (sign-out / invalid
// session). Also drop the cached image data URIs so no image bytes outlive the
// session. The base URL key is left so the login screen can prefill the last
// server; it carries no secret.
export async function clearSession(): Promise<void> {
  _cookieJar = null;
  clearImageCache();
  await SecureStore.deleteItemAsync(COOKIE_KEY);
}

// The last persisted base URL (for prefilling the login form), or null.
export async function getStoredBaseUrl(): Promise<string | null> {
  return SecureStore.getItemAsync(BASEURL_KEY);
}

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
    readonly body: unknown = null,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

async function request<T>(
  method: string,
  path: string,
  json?: unknown,
): Promise<T> {
  const headers: Record<string, string> = {};
  if (json !== undefined) {
    headers['content-type'] = 'application/json';
  }
  if (_cookieJar) {
    headers['cookie'] = _cookieJar;
  }

  const res = await fetch(`${_baseUrl}${path}`, {
    method,
    headers,
    body: json !== undefined ? JSON.stringify(json) : undefined,
    credentials: 'include',
  });

  // RN exposes Set-Cookie unlike browsers; capture it for subsequent requests.
  const setCookie = res.headers.get('set-cookie');
  if (setCookie) {
    // Keep only name=value pairs, strip directives (HttpOnly, Path, SameSite…).
    _cookieJar = setCookie
      .split(',')
      .map((c) => c.split(';')[0].trim())
      .join('; ');
  }

  if (res.status === 204) return undefined as T;

  const text = await res.text();
  let parsed: unknown = null;
  try {
    parsed = text ? (JSON.parse(text) as unknown) : null;
  } catch {
    parsed = text;
  }

  if (!res.ok) {
    throw new ApiError(
      res.status,
      `${method} ${path} → ${res.status}`,
      parsed,
    );
  }
  return parsed as T;
}

export function apiGet<T>(path: string): Promise<T> {
  return request<T>('GET', path);
}

export function apiPost<T>(path: string, json?: unknown): Promise<T> {
  return request<T>('POST', path, json);
}

// ---------------------------------------------------------------------------
// Authenticated image loading.
// ---------------------------------------------------------------------------

// NOTE: a header-based <Image> path (source = { uri, headers: { Cookie }}) was
// tried first but proved unreliable on Expo Go Android — the RN/Fresco image
// loader does not forward the custom Cookie header, so those requests 401 even
// though the same cookie works for fetch. The authenticated-fetch → data URI
// path below (fetchImageAsDataUri, used via imageLoader) is therefore the
// PRIMARY path for mobile thumbnails/previews.

// Diagnostic: fetch a URL with the SAME Cookie header used by API calls and
// report the HTTP status only. Used to distinguish "image auth fails" (401/403)
// from "no thumbnail" (404) from "endpoint error" (5xx) from "cookie missing".
// Returns status -1 if the request could not be made (network error).
export async function probe(
  path: string,
): Promise<{ status: number; ok: boolean; cookieSent: boolean }> {
  const headers: Record<string, string> = {};
  if (_cookieJar) headers['cookie'] = _cookieJar;
  try {
    const res = await fetch(`${_baseUrl}${path}`, {
      method: 'GET',
      headers,
      credentials: 'include',
    });
    return { status: res.status, ok: res.ok, cookieSent: _cookieJar !== null };
  } catch {
    return { status: -1, ok: false, cookieSent: _cookieJar !== null };
  }
}

// Fallback image loader: when RN <Image>/header auth does not work on a given
// runtime, fetch the bytes with the authenticated fetch (which DOES carry the
// cookie) and convert to a base64 data URI that <Image> can render with no
// headers at all. Used only on the <Image> onError path, so the normal header
// path is preferred and this cost is bounded to failures. Thumbnails are small;
// medium previews are larger but still bounded. Never used for originals.
export async function fetchImageAsDataUri(path: string): Promise<string> {
  const headers: Record<string, string> = {};
  if (_cookieJar) headers['cookie'] = _cookieJar;
  const res = await fetch(`${_baseUrl}${path}`, {
    method: 'GET',
    headers,
    credentials: 'include',
  });
  if (!res.ok) {
    throw new ApiError(res.status, `GET ${path} → ${res.status}`);
  }
  const blob = await res.blob();
  return await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onloadend = () => resolve(reader.result as string);
    reader.onerror = () => reject(new Error('Failed to read image bytes'));
    reader.readAsDataURL(blob);
  });
}

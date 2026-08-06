// NubArca TV API client.
//
// The cookie names below ("NubArca.TvSession", "NubArca.Auth") are SERVER
// contracts, not local choices: they must match Program.cs and TvPairingService
// exactly. The package-local AsyncStorage key is the released TV identity and is
// immutable for in-place upgrades. Changing any of the three un-pairs the fleet.
//
// Cookie handling: React Native's fetch does not maintain a browser-style
// cookie jar. The limited TV session is a SESSION COOKIE ("NubArca.TvSession",
// HttpOnly, path-scoped to /api/tv) set by the pairing-poll response. We capture
// Set-Cookie (RN exposes it, unlike browsers) and forward it manually via the
// Cookie request header on subsequent /api/tv calls.
//
// This is NOT token auth: there is no JWT/bearer. The cookie is held in memory
// and PERSISTED (unencrypted) across app restarts via AsyncStorage — see below.
//
// The TV app ONLY ever calls /api/tv/* endpoints. It has no access to the normal
// owner APIs and never sends the normal NubArca.Auth cookie.
//
// Persistence scope (deliberately narrow): only the limited NubArca.TvSession
// cookie string is persisted. By construction _cookieJar can hold nothing else —
// /api/tv responses only ever Set-Cookie the TV session; the owner NubArca.Auth
// cookie is never received here, the pairing secret travels in a header (never a
// cookie), and party tokens live in URLs (never cookies). Storage is AsyncStorage
// (device-local, app-private, NOT encrypted); on a shared/rooted device another
// app with the same uid or root could read it — acceptable for a limited,
// server-revocable, expiring TV session, and documented in tv/README.md.

import AsyncStorage from '@react-native-async-storage/async-storage';
import { Directory, File, Paths } from 'expo-file-system';
import { tvDebug } from '../debug';

// AsyncStorage key for the persisted limited TV session cookie. Bumping this
// string invalidates any previously persisted session.
//
// There is deliberately NO migration from the pre-NubArca key. That key only
// ever existed inside the retired TV package (named in tv/README.md), and an
// Android applicationId change gives the new package its own private storage
// sandbox — the old value is not reachable from here even if we wanted it.
// Every install of NubArca TV starts unpaired and pairs once.
//
// The cookie this holds is still named NubArca.TvSession: that is the backend
// wire contract with /api/tv/*, not a user-visible name, and renaming it would
// invalidate live sessions. It stays on the compatibility allowlist.
const SESSION_STORAGE_KEY = 'nubarca.tv.session.cookie';

let _baseUrl = '';
let _cookieJar: string | null = null;

export function configure(baseUrl: string): void {
  _baseUrl = baseUrl.replace(/\/$/, '');
}

export function getBaseUrl(): string {
  return _baseUrl;
}

export function hasSession(): boolean {
  return _cookieJar !== null;
}

// Video-hls slice 4: request headers carrying the limited TV session cookie,
// for consumers that stream /api/tv media through a NATIVE loader (the
// expo-video player) instead of the fetch stack above. The native player does
// not share RN fetch's cookie jar, so the manually-held cookie must ride along
// explicitly. Only ever pass these to URLs vetted by resolveTvMediaUrl —
// the same /api/tv-only boundary every other consumer obeys.
export function getTvSessionHeaders(): Record<string, string> {
  return _cookieJar ? { cookie: _cookieJar } : {};
}

// Headers for native media consumers. Personal video playback needs the same
// short-lived unlock grant as JSON/poster requests; expo-video does not share
// the fetch client, so both headers must be attached to master + HLS children.
export function getTvMediaHeaders(personal = false): Record<string, string> {
  const headers = personal ? { ...(_personalHeaderProvider?.() ?? {}) } : {};
  if (_cookieJar) headers.cookie = _cookieJar;
  return headers;
}

// Rehydrate the persisted TV session cookie into memory on app launch. Returns
// true when a stored session was found (the caller then validates it against
// GET /api/tv/session before trusting it). Best-effort: any storage error is
// swallowed and treated as "no persisted session".
export async function restoreSession(): Promise<boolean> {
  try {
    const stored = await AsyncStorage.getItem(SESSION_STORAGE_KEY);
    if (stored) {
      _cookieJar = stored;
      return true;
    }
  } catch {
    // No usable persisted session (storage unavailable / corrupt) → pair fresh.
  }
  return false;
}

// Drop the in-memory cookie AND remove the persisted copy, so a revoked/expired
// session does not survive a restart. Also purge any cached derived media so a
// revoked session cannot keep showing previously-fetched thumbnails/previews.
// Synchronous for callers; the storage/disk removals are fire-and-forget.
export function clearSession(): void {
  _cookieJar = null;
  void AsyncStorage.removeItem(SESSION_STORAGE_KEY).catch(() => { /* best effort */ });
  clearTvMediaCache();
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

// Guard: the TV app must never reach beyond the limited TV surface.
function assertTvPath(path: string): void {
  if (!path.startsWith('/api/tv/')) {
    throw new Error(`TV client refuses non-TV path: ${path}`);
  }
}

function captureCookie(setCookie: string | null): void {
  if (!setCookie) return;
  // Keep only name=value pairs, strip directives (HttpOnly, Path, SameSite…).
  _cookieJar = setCookie
    .split(',')
    .map((c) => c.split(';')[0].trim())
    .filter((c) => c.length > 0)
    .join('; ');
  // Persist the refreshed TV session cookie so it survives an app restart.
  // Fire-and-forget: a storage failure only means the session is not persisted.
  void AsyncStorage.setItem(SESSION_STORAGE_KEY, _cookieJar).catch(() => { /* best effort */ });
}

interface RequestOptions {
  method?: string;
  json?: unknown;
  headers?: Record<string, string>;
}

async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  assertTvPath(path);
  const headers: Record<string, string> = { ...opts.headers };
  if (opts.json !== undefined) headers['content-type'] = 'application/json';
  if (_cookieJar) headers['cookie'] = _cookieJar;

  const res = await fetch(`${_baseUrl}${path}`, {
    method: opts.method ?? 'GET',
    headers,
    body: opts.json !== undefined ? JSON.stringify(opts.json) : undefined,
    credentials: 'include',
  });

  captureCookie(res.headers.get('set-cookie'));

  if (res.status === 204) return undefined as T;

  const text = await res.text();
  let parsed: unknown = null;
  try {
    parsed = text ? (JSON.parse(text) as unknown) : null;
  } catch {
    parsed = text;
  }

  if (!res.ok) {
    throw new ApiError(res.status, `${opts.method ?? 'GET'} ${path} → ${res.status}`, parsed);
  }
  return parsed as T;
}

export function tvGet<T>(path: string, headers?: Record<string, string>): Promise<T> {
  return request<T>(path, { headers });
}

export function tvPost<T>(path: string, json?: unknown, headers?: Record<string, string>): Promise<T> {
  return request<T>(path, { method: 'POST', json, headers });
}

export function tvPut<T>(path: string, json?: unknown, headers?: Record<string, string>): Promise<T> {
  return request<T>(path, { method: 'PUT', json, headers });
}

export function tvDelete<T>(path: string, headers?: Record<string, string>): Promise<T> {
  return request<T>(path, { method: 'DELETE', headers });
}

// ---------------------------------------------------------------------------
// Authenticated DERIVED-media loading (thumbnails / previews / posters).
//
// The RN <Image> loader does not forward a custom Cookie header reliably, and the
// previous approach (fetch → Blob → FileReader.readAsDataURL) does not produce a
// usable data URI on the Fire TV / Hermes runtime — so images silently rendered
// blank even on HTTP 200. We still use authenticated fetch, but write its bytes
// to the app-private cache and hand <Image> a local file:// URI. This matters
// after a process restart: React Native's fetch retains the native HttpOnly
// session cookie, while expo-file-system's separate downloader does not share
// that cookie jar and would return 401 if the manually persisted cookie was
// unavailable.
//
// Only ever used for DERIVED media served under /api/tv/media — never original
// full-resolution bytes. Every URL is validated to stay on the configured API
// origin AND under /api/tv/ before any request is made.

const MEDIA_CACHE_DIRNAME = 'tv-media';
// Bound the on-disk cache; oldest entries are evicted (LRU-ish by last use).
const MEDIA_CACHE_MAX_ENTRIES = 200;
// Bound how many derived-media downloads run at once. A grid fires many at once;
// on the Fire Stick's weak SoC a small pool keeps the UI responsive and lets
// visible thumbnails resolve progressively.
const MEDIA_MAX_CONCURRENT = 3;
// A URL that just failed is not retried for this window (tiles remount when the
// virtualized grid recycles them; without this memo every scroll-back re-fired
// the same failing download in a loop).
const MEDIA_FAILURE_MEMO_MS = 45_000;

export type TvMediaPriority = 'high' | 'low';

export interface LoadTvMediaOptions {
  // 'high' = something the user is looking at (visible thumbnail, current
  // slideshow preview). 'low' = warm-up work (prev/next prefetch, fallbacks).
  // Low-priority requests only get a download slot when no high-priority
  // request is waiting.
  priority?: TvMediaPriority;
  // Checked after the request leaves the queue: lets an unmounted tile / a left
  // album drop its queued download instead of wasting the network slot.
  shouldAbort?: () => boolean;
  // Personal Gallery media: also send the in-memory Personal Area unlock grant
  // header (provider registered by api/personal.ts — read at REQUEST time, so a
  // re-unlock is picked up and a dropped grant sends nothing). The server
  // re-validates session + grant on every byte request regardless.
  personal?: boolean;
}

// Registered by api/personal.ts (which imports this module — the provider
// indirection avoids the import cycle). Returns {} when locked.
let _personalHeaderProvider: (() => Record<string, string>) | null = null;

export function setPersonalMediaHeaderProvider(provider: () => Record<string, string>): void {
  _personalHeaderProvider = provider;
}

// Thrown (never memoized as a failure) when shouldAbort() cancels a queued load.
export class MediaAborted extends Error {
  constructor() {
    super('TV media load aborted');
    this.name = 'MediaAborted';
  }
}

let _mediaDir: Directory | null = null;
// Dedupe concurrent loads of the same media (e.g. a grid mounting many tiles).
const _mediaInflight = new Map<string, Promise<string>>();
// Insertion/last-use order of cache keys currently on disk, for bounded eviction.
let _mediaOrder: string[] = [];
// Recent failures by cache key → timestamp (see MEDIA_FAILURE_MEMO_MS).
const _mediaFailures = new Map<string, number>();
// Two-level async semaphore for the download pool: high-priority waiters are
// always served before low-priority ones.
let _mediaActive = 0;
const _hiWaiters: Array<() => void> = [];
const _loWaiters: Array<() => void> = [];

function acquireDownloadSlot(priority: TvMediaPriority): Promise<void> {
  if (_mediaActive < MEDIA_MAX_CONCURRENT) {
    _mediaActive += 1;
    return Promise.resolve();
  }
  return new Promise<void>((resolve) => {
    (priority === 'high' ? _hiWaiters : _loWaiters).push(resolve);
  });
}

function releaseDownloadSlot(): void {
  const next = _hiWaiters.shift() ?? _loWaiters.shift();
  if (next) {
    next(); // hand the active slot straight to the next waiter (count unchanged)
  } else {
    _mediaActive = Math.max(0, _mediaActive - 1);
  }
}

// Debug-only: the media variant, derived from the URL suffix (safe to log).
function mediaVariant(url: string): string {
  if (url.endsWith('/thumbnail')) return 'thumbnail';
  if (url.endsWith('/preview')) return 'preview';
  if (url.endsWith('/poster')) return 'poster';
  return 'other';
}

// Resolve a TV media path/URL to an absolute URL, enforcing the TV boundary:
// a relative "/api/tv/..." path is joined to the configured base; an absolute
// URL is accepted only if it stays on the configured origin AND under /api/tv/.
// Anything else throws (the loader refuses non-TV media, mirroring assertTvPath).
export function resolveTvMediaUrl(path: string): string {
  if (path.startsWith('/api/tv/')) return `${_baseUrl}${path}`;
  if (_baseUrl && path.startsWith(`${_baseUrl}/api/tv/`)) return path;
  throw new Error('TV media loader refuses non-TV media path');
}

// Small deterministic filename for a resolved media URL. The cache lives in the
// app-private cache dir and is never exposed/logged; the key is opaque.
function mediaCacheKey(url: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < url.length; i += 1) {
    h ^= url.charCodeAt(i);
    h = Math.imul(h, 0x01000193);
  }
  return `${(h >>> 0).toString(36)}${url.length.toString(36)}`;
}

function ensureMediaDir(): Directory {
  if (_mediaDir) return _mediaDir;
  const dir = new Directory(Paths.cache, MEDIA_CACHE_DIRNAME);
  try {
    dir.create({ intermediates: true, idempotent: true });
  } catch {
    // Directory already exists / transient FS error — the download will surface
    // any real problem.
  }
  _mediaDir = dir;
  return dir;
}

function rememberCacheEntry(key: string): void {
  const at = _mediaOrder.indexOf(key);
  if (at >= 0) _mediaOrder.splice(at, 1);
  _mediaOrder.push(key);
  while (_mediaOrder.length > MEDIA_CACHE_MAX_ENTRIES) {
    const evict = _mediaOrder.shift();
    if (!evict || !_mediaDir) continue;
    try {
      const stale = new File(_mediaDir, `${evict}.img`);
      if (stale.exists) stale.delete();
    } catch {
      // best effort
    }
  }
}

async function downloadTvMedia(
  url: string,
  key: string,
  opts: LoadTvMediaOptions,
): Promise<string> {
  const variant = mediaVariant(url);
  const dir = ensureMediaDir();
  const dest = new File(dir, `${key}.img`);
  // Cache hit: reuse the already-downloaded file (no download slot needed).
  try {
    if (dest.exists && (dest.size ?? 0) > 0) {
      rememberCacheEntry(key);
      tvDebug('media', variant, key, 'cache-hit');
      return dest.uri;
    }
  } catch {
    // fall through to a fresh download
  }
  // Throttle concurrent network downloads so a large grid loads progressively
  // instead of flooding the downloader; high-priority (visible) media first.
  const queuedAt = Date.now();
  await acquireDownloadSlot(opts.priority ?? 'high');
  const startedAt = Date.now();
  try {
    // The consumer may have unmounted (tile scrolled away, album left) while we
    // waited in the queue — drop the download instead of wasting the slot.
    if (opts.shouldAbort?.()) throw new MediaAborted();
    const headers: Record<string, string> = opts.personal
      ? { ...(_personalHeaderProvider?.() ?? {}) }
      : {};
    if (_cookieJar) headers.cookie = _cookieJar;
    // Use the same authenticated fetch stack as the JSON API so the native
    // HttpOnly cookie jar is honored even when `_cookieJar` could not be
    // rehydrated. Derived previews are bounded server-side; concurrency is also
    // capped above, so writing the response bytes does not fan out unbounded.
    const response = await fetch(url, { headers, credentials: 'include' });
    captureCookie(response.headers.get('set-cookie'));
    if (!response.ok) throw new ApiError(response.status, `GET TV media → ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength <= 0) {
      throw new ApiError(0, 'TV media download produced no bytes');
    }
    dest.create({ intermediates: true, overwrite: true });
    dest.write(bytes);
    const file = dest;
    rememberCacheEntry(key);
    _mediaFailures.delete(key);
    tvDebug(
      'media', variant, key, 'ok',
      'wait', startedAt - queuedAt, 'dl', Date.now() - startedAt,
      'bytes', file.size ?? -1, 'active', _mediaActive,
    );
    return file.uri;
  } catch (err) {
    if (err instanceof MediaAborted) {
      tvDebug('media', variant, key, 'aborted', 'wait', startedAt - queuedAt);
    } else {
      // Memoize the failure so remounting tiles do not retry in a loop. The memo
      // is per exact URL (key includes the variant), so a failed thumbnail never
      // poisons the same item's preview.
      _mediaFailures.set(key, Date.now());
      tvDebug(
        'media', variant, key, 'fail',
        'wait', startedAt - queuedAt, 'dl', Date.now() - startedAt,
        err instanceof ApiError ? `status=${err.status}` : (err as Error).name ?? 'error',
      );
    }
    throw err;
  } finally {
    releaseDownloadSlot();
  }
}

// Load a derived TV media file and return a local file:// URI for <Image>.
// Concurrent requests for the same media share one download; recent failures
// reject immediately (no retry loop). Rejects (→ caller shows a placeholder) on
// a non-TV path, a non-2xx response, or empty bytes.
export function loadTvMedia(path: string, opts: LoadTvMediaOptions = {}): Promise<string> {
  let url: string;
  try {
    url = resolveTvMediaUrl(path);
  } catch (err) {
    return Promise.reject(err);
  }
  const key = mediaCacheKey(url);
  const inflight = _mediaInflight.get(key);
  if (inflight) return inflight;
  const failedAt = _mediaFailures.get(key);
  if (failedAt !== undefined) {
    if (Date.now() - failedAt < MEDIA_FAILURE_MEMO_MS) {
      return Promise.reject(new ApiError(0, 'TV media recently failed'));
    }
    _mediaFailures.delete(key);
  }
  const task = downloadTvMedia(url, key, opts).finally(() => _mediaInflight.delete(key));
  _mediaInflight.set(key, task);
  return task;
}

// Purge all cached derived media (called when the TV session is cleared/revoked/
// expired). Best-effort: drops in-flight tracking and deletes the cache dir.
export function clearTvMediaCache(): void {
  _mediaInflight.clear();
  _mediaFailures.clear();
  _mediaOrder = [];
  try {
    const dir = _mediaDir ?? new Directory(Paths.cache, MEDIA_CACHE_DIRNAME);
    if (dir.exists) dir.delete();
  } catch {
    // best effort
  }
  _mediaDir = null;
}

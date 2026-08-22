// NubArca TV API client.
//
// The cookie names below ("NubArca.TvSession", "NubArca.Auth") are SERVER
// contracts, not local choices: they must match Program.cs and TvPairingService
// exactly. The package-local AsyncStorage key is the released TV identity and is
// immutable for in-place upgrades. Changing any of the three un-pairs the fleet.
//
// Cookie handling deliberately has one authority. The limited TV session is a
// SESSION COOKIE ("NubArca.TvSession", HttpOnly, path-scoped to /api/tv) set by
// the pairing-poll response. We capture its exact name=value, persist it, and
// forward it manually; native fetch's separate cookie jar is disabled.
//
// This is NOT token auth: there is no JWT/bearer. The cookie is held in memory
// and PERSISTED (unencrypted) across app restarts via AsyncStorage — see below.
//
// The TV app ONLY ever calls /api/tv/* endpoints. It has no access to the normal
// owner APIs and never sends the normal NubArca.Auth cookie.
//
// Persistence scope (deliberately narrow): only the limited NubArca.TvSession
// cookie string is persisted. By construction the session store holds nothing else —
// /api/tv responses only ever Set-Cookie the TV session; the owner NubArca.Auth
// cookie is never received here, the pairing secret travels in a header (never a
// cookie), and party tokens live in URLs (never cookies). Storage is AsyncStorage
// (device-local, app-private, NOT encrypted); on a shared/rooted device another
// app with the same uid or root could read it — acceptable for a limited,
// server-revocable, expiring TV session, and documented in tv/README.md.

import AsyncStorage from '@react-native-async-storage/async-storage';
import { Directory, File, Paths } from 'expo-file-system';
import { tvDebug } from '../debug';
import { TvSessionCookieStore } from './sessionCookie';
import {
  createMediaSubscribers,
  handoffFirstLive,
  isSoleLease,
  oldestEvictableIndex,
  type MediaWaiter,
} from '../media/mediaCachePolicy';

// There is deliberately NO migration from the pre-NubArca key. That key only
// ever existed inside the retired TV package (named in tv/README.md), and an
// Android applicationId change gives the new package its own private storage
// sandbox — the old value is not reachable from here even if we wanted it.
// Every install of NubArca TV starts unpaired and pairs once.
//
// The cookie this holds is still named NubArca.TvSession: that is the backend
// wire contract with /api/tv/*, not a user-visible name, and renaming it would
// invalidate live sessions. It stays on the compatibility allowlist.
let _baseUrl = '';
const sessionCookie = new TvSessionCookieStore(AsyncStorage);

export function configure(baseUrl: string): void {
  _baseUrl = baseUrl.replace(/\/$/, '');
}

export function getBaseUrl(): string {
  return _baseUrl;
}

// Headers for native media consumers. Personal video playback needs the same
// short-lived unlock grant as JSON/poster requests; expo-video does not share
// the fetch client, so both headers must be attached to master + HLS children.
export function getTvMediaHeaders(personal = false): Record<string, string> {
  const headers = personal ? { ...(_personalHeaderProvider?.() ?? {}) } : {};
  if (sessionCookie.current) headers.cookie = sessionCookie.current;
  return headers;
}

// Rehydrate the persisted TV session cookie into memory on app launch. Returns
// true when a stored session was found (the caller then validates it against
// GET /api/tv/session before trusting it). Best-effort: any storage error is
// swallowed and treated as "no persisted session".
export async function restoreSession(): Promise<boolean> {
  return sessionCookie.restore();
}

// Drop the in-memory cookie AND remove the persisted copy, so a revoked/expired
// session does not survive a restart. Also purge any cached derived media so a
// revoked session cannot keep showing previously-fetched thumbnails/previews.
// Synchronous for callers; the storage/disk removals are fire-and-forget.
export function clearSession(): void {
  sessionCookie.clear();
  clearTvMediaCache();
}

// Pairing is not complete until the exact limited-session cookie is durable.
// A failed first write is retried here before the UI leaves the pairing screen.
export async function ensureSessionPersisted(): Promise<() => boolean> {
  const generation = await sessionCookie.ensure();
  return () => sessionCookie.isCurrent(generation);
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

async function captureCookie(setCookie: string | null): Promise<void> {
  await sessionCookie.capture(setCookie);
}

interface RequestOptions {
  method?: string;
  json?: unknown;
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

async function request<T>(path: string, opts: RequestOptions = {}): Promise<T> {
  assertTvPath(path);
  const headers: Record<string, string> = { ...opts.headers };
  if (opts.json !== undefined) headers['content-type'] = 'application/json';
  if (sessionCookie.current) headers['cookie'] = sessionCookie.current;

  const res = await fetch(`${_baseUrl}${path}`, {
    method: opts.method ?? 'GET',
    headers,
    body: opts.json !== undefined ? JSON.stringify(opts.json) : undefined,
    credentials: 'omit',
    signal: opts.signal,
  });

  await captureCookie(res.headers.get('set-cookie'));

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

export function tvGet<T>(
  path: string,
  headers?: Record<string, string>,
  signal?: AbortSignal,
): Promise<T> {
  return request<T>(path, { headers, signal });
}

export function tvPost<T>(
  path: string,
  json?: unknown,
  headers?: Record<string, string>,
  signal?: AbortSignal,
): Promise<T> {
  return request<T>(path, { method: 'POST', json, headers, signal });
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
// after a process restart: the session store rehydrates the exact manual cookie,
// while expo-file-system's separate downloader would otherwise return 401.
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
  // Personal Gallery media: also send the in-memory Personal Area unlock grant
  // header (provider registered by api/personal.ts — read at REQUEST time, so a
  // re-unlock is picked up and a dropped grant sends nothing). The server
  // re-validates session + grant on every byte request regardless.
  personal?: boolean;
}

export interface TvMediaLease {
  readonly uri: string;
  retain: () => TvMediaLease | null;
  invalidate: () => boolean;
  release: () => void;
}

export interface TvMediaLeaseRequest {
  readonly result: Promise<TvMediaLease>;
  cancel: () => void;
}

// Registered by api/personal.ts (which imports this module — the provider
// indirection avoids the import cycle). Returns {} when locked.
let _personalHeaderProvider: (() => Record<string, string>) | null = null;

export function setPersonalMediaHeaderProvider(provider: () => Record<string, string>): void {
  _personalHeaderProvider = provider;
}

let _mediaDir: Directory | null = null;
// Dedupe concurrent loads of the same media (e.g. a grid mounting many tiles).
interface MediaInflight {
  readonly subscribers: ReturnType<typeof createMediaSubscribers>;
  task: Promise<string>;
}
const _mediaInflight = new Map<string, MediaInflight>();
// Insertion/last-use order of cache keys currently on disk, for bounded eviction.
let _mediaOrder: string[] = [];
// Files currently handed to a mounted image. Eviction may temporarily exceed the
// soft limit rather than deleting bytes from under a live file:// URI.
const _mediaLeases = new Map<string, number>();
// Invalidates every queued/active cache task when a session clear deletes the
// directory. Old tasks may finish their network request, but can never write or
// mutate the new generation.
let _mediaEpoch = 0;
// Recent failures by cache key → timestamp (see MEDIA_FAILURE_MEMO_MS).
const _mediaFailures = new Map<string, number>();
// Two-level async semaphore for the download pool: high-priority waiters are
// always served before low-priority ones.
let _mediaActive = 0;
const _hiWaiters: MediaWaiter[] = [];
const _loWaiters: MediaWaiter[] = [];

function acquireDownloadSlot(
  priority: TvMediaPriority,
  canStart: () => boolean,
  orphan: () => void,
): Promise<boolean> {
  if (!canStart()) {
    orphan();
    return Promise.resolve(false);
  }
  if (_mediaActive < MEDIA_MAX_CONCURRENT) {
    _mediaActive += 1;
    return Promise.resolve(true);
  }
  return new Promise<boolean>((resolve) => {
    (priority === 'high' ? _hiWaiters : _loWaiters).push({
      canStart,
      start: () => { resolve(true); },
      discard: () => {
        orphan();
        resolve(false);
      },
    });
  });
}

function releaseDownloadSlot(): void {
  // A handoff keeps the active count unchanged. If every waiter is stale, the
  // helper discards them in this same turn and the slot is actually released.
  if (!handoffFirstLive(_hiWaiters, _loWaiters)) {
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

function deleteCacheEntry(key: string): void {
  _mediaOrder = _mediaOrder.filter((candidate) => candidate !== key);
  _mediaFailures.delete(key);
  if (!_mediaDir) return;
  try {
    const stale = new File(_mediaDir, `${key}.img`);
    if (stale.exists) stale.delete();
  } catch {
    // best effort
  }
}

function trimMediaCache(protectedKey?: string): void {
  while (_mediaOrder.length > MEDIA_CACHE_MAX_ENTRIES) {
    const at = oldestEvictableIndex(_mediaOrder, _mediaLeases, protectedKey);
    if (at < 0) return;
    const [key] = _mediaOrder.splice(at, 1);
    if (!_mediaDir) continue;
    try {
      const stale = new File(_mediaDir, `${key}.img`);
      if (stale.exists) stale.delete();
    } catch {
      // best effort
    }
  }
}

function rememberCacheEntry(key: string): void {
  const at = _mediaOrder.indexOf(key);
  if (at >= 0) _mediaOrder.splice(at, 1);
  _mediaOrder.push(key);
  trimMediaCache(key);
}

function retainMediaKey(key: string): void {
  _mediaLeases.set(key, (_mediaLeases.get(key) ?? 0) + 1);
}

function releaseMediaKey(key: string): void {
  const count = _mediaLeases.get(key) ?? 0;
  if (count <= 1) _mediaLeases.delete(key);
  else _mediaLeases.set(key, count - 1);
  trimMediaCache();
}

function mediaLease(key: string, uri: string, epoch: number): TvMediaLease {
  let active = true;
  return {
    uri,
    retain: () => {
      if (!active || epoch !== _mediaEpoch) return null;
      retainMediaKey(key);
      return mediaLease(key, uri, epoch);
    },
    // The caller still owns its lease here. Refuse to remove bytes when any
    // other mounted consumer also owns this exact local file.
    invalidate: () => {
      if (!active || epoch !== _mediaEpoch || !isSoleLease(_mediaLeases, key)) return false;
      deleteCacheEntry(key);
      return true;
    },
    release: () => {
      if (!active) return;
      active = false;
      if (epoch === _mediaEpoch) releaseMediaKey(key);
    },
  };
}

class MediaCacheReset extends Error {
  constructor() {
    super('TV media cache generation changed');
    this.name = 'MediaCacheReset';
  }
}

const MEDIA_WITHOUT_SUBSCRIBERS = new Error('TV media request has no subscribers');

async function downloadTvMedia(
  url: string,
  key: string,
  opts: LoadTvMediaOptions,
  epoch: number,
  hasSubscribers: () => boolean,
  orphan: () => void,
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
  const acquired = await acquireDownloadSlot(
    opts.priority ?? 'high',
    () => epoch === _mediaEpoch && hasSubscribers(),
    orphan,
  );
  if (!acquired) {
    throw epoch === _mediaEpoch ? MEDIA_WITHOUT_SUBSCRIBERS : new MediaCacheReset();
  }
  const startedAt = Date.now();
  try {
    if (epoch !== _mediaEpoch) throw new MediaCacheReset();
    if (!hasSubscribers()) {
      orphan();
      throw MEDIA_WITHOUT_SUBSCRIBERS;
    }
    const headers: Record<string, string> = opts.personal
      ? { ...(_personalHeaderProvider?.() ?? {}) }
      : {};
    if (sessionCookie.current) headers.cookie = sessionCookie.current;
    // Use the same single-authority manual cookie as the JSON API. Derived
    // previews are bounded server-side; concurrency is also capped above, so
    // writing the response bytes does not fan out unbounded.
    const response = await fetch(url, { headers, credentials: 'omit' });
    if (epoch !== _mediaEpoch) throw new MediaCacheReset();
    if (!hasSubscribers()) {
      orphan();
      throw MEDIA_WITHOUT_SUBSCRIBERS;
    }
    await captureCookie(response.headers.get('set-cookie'));
    if (!response.ok) throw new ApiError(response.status, `GET TV media → ${response.status}`);
    const bytes = new Uint8Array(await response.arrayBuffer());
    if (bytes.byteLength <= 0) {
      throw new ApiError(0, 'TV media download produced no bytes');
    }
    if (epoch !== _mediaEpoch) throw new MediaCacheReset();
    if (!hasSubscribers()) {
      orphan();
      throw MEDIA_WITHOUT_SUBSCRIBERS;
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
    if (err === MEDIA_WITHOUT_SUBSCRIBERS || !hasSubscribers()) {
      // An orphan is lifecycle, not a media/server failure. Never poison the
      // URL's retry window: a late/new subscriber must start a fresh task.
      orphan();
      tvDebug('media', variant, key, 'no-subscribers');
      throw MEDIA_WITHOUT_SUBSCRIBERS;
    }
    if (err instanceof MediaCacheReset || epoch !== _mediaEpoch) {
      tvDebug('media', variant, key, 'cache-reset');
      throw err instanceof MediaCacheReset ? err : new MediaCacheReset();
    }
    // Memoize the failure so remounting tiles do not retry in a loop. The memo
    // is per exact URL (key includes the variant), so a failed thumbnail never
    // poisons the same item's preview.
    _mediaFailures.set(key, Date.now());
    tvDebug(
      'media', variant, key, 'fail',
      'wait', startedAt - queuedAt, 'dl', Date.now() - startedAt,
      err instanceof ApiError ? `status=${err.status}` : (err as Error).name ?? 'error',
    );
    throw err;
  } finally {
    releaseDownloadSlot();
  }
}

interface ResolvedMediaSubscription {
  readonly result: Promise<string>;
  release: () => void;
}

function subscribeResolvedTvMedia(
  url: string,
  key: string,
  opts: LoadTvMediaOptions,
): ResolvedMediaSubscription {
  const inflight = _mediaInflight.get(key);
  if (inflight) {
    return { result: inflight.task, release: inflight.subscribers.acquire() };
  }
  const failedAt = _mediaFailures.get(key);
  if (failedAt !== undefined) {
    if (Date.now() - failedAt < MEDIA_FAILURE_MEMO_MS) {
      return {
        result: Promise.reject(new ApiError(0, 'TV media recently failed')),
        release: () => {},
      };
    }
    _mediaFailures.delete(key);
  }
  const epoch = _mediaEpoch;
  const subscribers = createMediaSubscribers();
  const release = subscribers.acquire();
  const entry: MediaInflight = {
    subscribers,
    task: Promise.resolve(''),
  };
  const orphan = () => {
    if (_mediaInflight.get(key) === entry) _mediaInflight.delete(key);
  };
  entry.task = downloadTvMedia(url, key, opts, epoch, subscribers.hasAny, orphan)
    .then((uri) => {
      if (epoch !== _mediaEpoch) throw new MediaCacheReset();
      return uri;
    })
    .finally(() => {
      if (_mediaInflight.get(key) === entry) _mediaInflight.delete(key);
    });
  _mediaInflight.set(key, entry);
  return { result: entry.task, release };
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
  // Warm-up is a real, temporary subscriber: it keeps shared work alive only
  // until its own promise settles.
  const subscription = subscribeResolvedTvMedia(url, key, opts);
  return subscription.result.finally(subscription.release);
}

// Reserve cache ownership and subscribe synchronously. `cancel` releases both
// immediately on unmount; it never aborts work still owned by another tile.
export function loadTvMediaLeased(
  path: string,
  opts: LoadTvMediaOptions = {},
): TvMediaLeaseRequest {
  let url: string;
  try {
    url = resolveTvMediaUrl(path);
  } catch (err) {
    return { result: Promise.reject(err), cancel: () => {} };
  }
  const key = mediaCacheKey(url);
  const epoch = _mediaEpoch;
  retainMediaKey(key);
  const subscription = subscribeResolvedTvMedia(url, key, opts);
  let reserved = true;
  let cancelled = false;
  let resolvedLease: TvMediaLease | null = null;

  const releaseReservation = () => {
    if (!reserved) return;
    reserved = false;
    if (epoch === _mediaEpoch) releaseMediaKey(key);
  };
  const result = (async () => {
    try {
      const uri = await subscription.result;
      subscription.release();
      if (cancelled) throw MEDIA_WITHOUT_SUBSCRIBERS;
      if (epoch !== _mediaEpoch) throw new MediaCacheReset();
      reserved = false; // the returned token owns the existing reservation
      resolvedLease = mediaLease(key, uri, epoch);
      return resolvedLease;
    } catch (err) {
      subscription.release();
      releaseReservation();
      throw err;
    }
  })();

  return {
    result,
    cancel: () => {
      if (cancelled) return;
      cancelled = true;
      subscription.release();
      if (resolvedLease) resolvedLease.release();
      else releaseReservation();
    },
  };
}

// Purge all cached derived media (called when the TV session is cleared/revoked/
// expired). Best-effort: drops in-flight tracking and deletes the cache dir.
function clearTvMediaCache(): void {
  _mediaEpoch += 1;
  _mediaInflight.clear();
  _mediaFailures.clear();
  _mediaLeases.clear();
  _mediaOrder = [];
  try {
    const dir = _mediaDir ?? new Directory(Paths.cache, MEDIA_CACHE_DIRNAME);
    if (dir.exists) dir.delete();
  } catch {
    // best effort
  }
  _mediaDir = null;
}

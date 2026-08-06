// Centralized authenticated-image loader for the mobile gallery.
//
// WHY THIS EXISTS / INVESTIGATION SUMMARY
// On Expo Go Android, React Native's <Image> loader does not reliably forward a
// custom `Cookie` header, so direct/header image auth fails even though the
// cookie is valid for fetch (the gallery JSON, /api/auth/me, and these fetches
// all succeed with the same jar). Therefore the authenticated-fetch → base64
// data URI path is the EXPLICIT PRIMARY path for mobile thumbnails/previews —
// not a fallback. <Image> then renders a data URI with no headers needed.
//
// This module makes that path robust across initial load, long scroll, FlatList
// virtualization, orientation changes (full remount), and viewer open/close:
//   * Bounded LRU cache keyed by the logical API path — a remounted tile renders
//     instantly from cache, with no refetch (fixes rotation placeholders).
//   * In-flight de-duplication — many tiles requesting the same path share one
//     fetch.
//   * Bounded concurrency — a semaphore caps simultaneous fetches so a rotation
//     remount cannot trigger a fetch storm.
//   * Retry with backoff for transient errors (network / 5xx); 401/403/404 are
//     permanent and not retried.
//   * Generation guard — clearing the cache (logout) discards any in-flight
//     result so post-logout bytes never repopulate the cache.
//
// Keys are logical owner-private API paths only (e.g.
// /api/files/{id}/thumbnail?size=small) — never storage internals. Only
// thumbnails/medium previews are cached; never originals.

import { fetchImageAsDataUri, ApiError } from './client';

const MAX_ENTRIES = 250; // ~small thumbnails dominate; bounded, a few MB.
const MAX_CONCURRENT = 6;
const MAX_ATTEMPTS = 3;
const BACKOFF_MS = 200;

// Insertion order = recency. A get-hit re-inserts as most-recent; a set over
// capacity evicts the oldest (first) key.
const _cache = new Map<string, string>();
const _inflight = new Map<string, Promise<string>>();
let _generation = 0;

// Lightweight, non-sensitive diagnostics (counts only — no paths, no bytes).
const _stats = { hits: 0, misses: 0, fetches: 0, failures: 0 };

export interface ImageStats {
  cached: number;
  inFlight: number;
  hits: number;
  misses: number;
  fetches: number;
  failures: number;
}

export function getImageStats(): ImageStats {
  return {
    cached: _cache.size,
    inFlight: _inflight.size,
    hits: _stats.hits,
    misses: _stats.misses,
    fetches: _stats.fetches,
    failures: _stats.failures,
  };
}

export function getCachedImage(path: string): string | undefined {
  const hit = _cache.get(path);
  if (hit === undefined) return undefined;
  _cache.delete(path);
  _cache.set(path, hit);
  return hit;
}

function setCachedImage(path: string, dataUri: string): void {
  if (_cache.has(path)) _cache.delete(path);
  _cache.set(path, dataUri);
  while (_cache.size > MAX_ENTRIES) {
    const oldest = _cache.keys().next().value;
    if (oldest === undefined) break;
    _cache.delete(oldest);
  }
}

// Clears cached + in-flight bytes and bumps the generation so any fetch already
// running resolves without repopulating the cache. Called on logout / session
// clear so no image bytes outlive the session.
export function clearImageCache(): void {
  _cache.clear();
  _inflight.clear();
  _generation += 1;
  _stats.hits = 0;
  _stats.misses = 0;
  _stats.fetches = 0;
  _stats.failures = 0;
}

// --- bounded-concurrency semaphore -----------------------------------------
let _active = 0;
const _waiters: Array<() => void> = [];

function acquire(): Promise<void> {
  if (_active < MAX_CONCURRENT) {
    _active += 1;
    return Promise.resolve();
  }
  return new Promise<void>((resolve) => _waiters.push(resolve));
}

function release(): void {
  const next = _waiters.shift();
  if (next) next(); // hand the slot directly to the next waiter
  else _active -= 1;
}

const delay = (ms: number): Promise<void> =>
  new Promise((resolve) => setTimeout(resolve, ms));

function isPermanent(err: unknown): boolean {
  return (
    err instanceof ApiError &&
    (err.status === 401 || err.status === 403 || err.status === 404)
  );
}

async function fetchWithRetry(path: string): Promise<string> {
  let lastErr: unknown;
  for (let attempt = 0; attempt < MAX_ATTEMPTS; attempt += 1) {
    try {
      _stats.fetches += 1;
      return await fetchImageAsDataUri(path);
    } catch (err) {
      lastErr = err;
      if (isPermanent(err)) throw err; // no point retrying auth/not-found
      if (attempt < MAX_ATTEMPTS - 1) await delay(BACKOFF_MS * (attempt + 1));
    }
  }
  throw lastErr;
}

// Resolve a path to a renderable data URI: cache → shared in-flight fetch →
// concurrency-limited fetch with retry. Throws only after retries are exhausted
// (or immediately on a permanent 401/403/404).
export function loadImage(path: string): Promise<string> {
  const cached = getCachedImage(path);
  if (cached !== undefined) {
    _stats.hits += 1;
    return Promise.resolve(cached);
  }
  const existing = _inflight.get(path);
  if (existing !== undefined) return existing;

  _stats.misses += 1;
  const startGen = _generation;
  const task = (async () => {
    await acquire();
    try {
      const uri = await fetchWithRetry(path);
      // Only cache if no logout/clear happened while we were fetching.
      if (_generation === startGen) setCachedImage(path, uri);
      return uri;
    } catch (err) {
      _stats.failures += 1;
      throw err;
    } finally {
      release();
      _inflight.delete(path);
    }
  })();
  _inflight.set(path, task);
  return task;
}

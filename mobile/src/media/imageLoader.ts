// Centralized authenticated-image loader.
//
// WHY THIS EXISTS / INVESTIGATION SUMMARY
// On Expo Go Android, React Native's <Image> loader does not reliably forward a
// custom `Cookie` header, so direct/header image auth fails even though the
// cookie is valid for fetch (the gallery JSON, /api/auth/me, and these fetches
// all succeed with the same jar). expo-image's header support is UNPROVEN on
// our Android target in this slice — until it is proven on hardware, this
// authenticated-fetch → base64 data URI path is the EXPLICIT PRIMARY path for
// mobile thumbnails/previews, not a fallback. <Image> then renders a data URI
// with no headers needed.
//
// Robustness rules:
//   * Bounded LRU cache keyed by logical API path — a remounted tile renders
//     instantly from cache with no refetch (fixes rotation placeholders);
//   * In-flight de-duplication — many tiles requesting one path share a fetch;
//   * Bounded concurrency — a semaphore caps simultaneous fetches so a
//     rotation remount cannot trigger a fetch storm;
//   * Retry with backoff for transient errors (network / 5xx); 401/403/404 are
//     permanent and never retried;
//   * Generation guard — logout discards in-flight results so post-logout
//     bytes never repopulate the cache;
//   * Keys are logical owner-private API paths only; only thumbnails/medium
//     previews are cached — never originals, never videos.

import { ApiError } from '../api/client.ts';
import { sessionCookieSource } from '../api/sessionAccess.ts';
import { authenticatedSource } from './imageSource.ts';

const MAX_ENTRIES = 250; // small thumbnails dominate; bounded, a few MB
const MAX_CONCURRENT = 6;
const MAX_ATTEMPTS = 3;
const BACKOFF_MS = 200;

const _cache = new Map<string, string>();
const _inflight = new Map<string, Promise<string>>();
let _generation = 0;
let _active = 0;
const _waiters: Array<() => void> = [];

function acquireSlot(): Promise<void> {
  if (_active < MAX_CONCURRENT) {
    _active += 1;
    return Promise.resolve();
  }
  return new Promise((resolve) => {
    _waiters.push(() => {
      _active += 1;
      resolve();
    });
  });
}

function releaseSlot(): void {
  const next = _waiters.shift();
  if (next) next();
  else _active -= 1;
}

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

// Logout path. Bumps the generation so every in-flight load becomes a
// discarded result, then drops all cached bytes.
export function clearImageCache(): void {
  _generation += 1;
  _cache.clear();
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

async function fetchBytes(path: string): Promise<string> {
  await acquireSlot();
  try {
    // Snapshot the source ONCE per attempt: a mid-flight logout must not let
    // a new session's cookie leak into this load's request.
    const src = authenticatedSource(path);
    if (!src) throw new ApiError(401, `GET ${path} → 401 (no session cookie)`);
    const res = await fetch(src.uri, {
      method: 'GET',
      headers: src.headers,
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
  } finally {
    releaseSlot();
  }
}

function isTransient(err: unknown): boolean {
  if (err instanceof ApiError) return err.status >= 500 || err.status === 429;
  // Fetch network failures surface as TypeError in RN/Hermes.
  return !(err instanceof ApiError);
}

async function fetchWithRetry(path: string): Promise<string> {
  let lastError: unknown;
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt += 1) {
    try {
      return await fetchBytes(path);
    } catch (err) {
      lastError = err;
      if (!isTransient(err)) throw err;
      if (attempt < MAX_ATTEMPTS) await sleep(BACKOFF_MS * attempt);
    }
  }
  throw lastError;
}

// Load one derivative as a renderable data URI, deduped and cached.
export async function loadImage(path: string): Promise<string> {
  const generation = _generation;
  const hit = _cache.get(path);
  if (hit !== undefined) {
    // Re-insert as most-recent.
    _cache.delete(path);
    _cache.set(path, hit);
    _stats.hits += 1;
    return hit;
  }
  _stats.misses += 1;

  const existing = _inflight.get(path);
  if (existing) return existing;

  const promise = (async () => {
    try {
      const uri = await fetchWithRetry(path);
      if (generation === _generation) {
        _cache.set(path, uri);
        while (_cache.size > MAX_ENTRIES) {
          const oldest = _cache.keys().next().value;
          if (oldest === undefined) break;
          _cache.delete(oldest);
        }
        _stats.fetches += 1;
      }
      return uri;
    } catch (err) {
      if (generation === _generation) _stats.failures += 1;
      throw err;
    } finally {
      _inflight.delete(path);
    }
  })();
  _inflight.set(path, promise);
  return promise;
}

// True when the session source still holds an owner cookie — used by screens
// to distinguish "signed out" from "load failed".
export function hasSession(): boolean {
  return sessionCookieSource().current !== null;
}

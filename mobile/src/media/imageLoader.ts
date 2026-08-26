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
// Byte ceiling on cached data URIs (approximate decoded payload budget).
// Entry-count alone cannot bound memory because medium previews vary widely
// in size; the byte cap is what actually protects the JS heap.
const MAX_TOTAL_BYTES = 48 * 1024 * 1024;
const MAX_CONCURRENT = 6;
const MAX_ATTEMPTS = 3;
const BACKOFF_MS = 200;
// Per-attempt wall-clock cap. A hung connection must never hold one of the
// few semaphore slots forever (the API client enforces the same discipline
// for JSON calls; image bytes simply get a larger budget).
const REQUEST_TIMEOUT_MS = 30_000;

// Test-only knobs. Production code never touches them; node --test uses them
// to exercise eviction and timeout paths without waiting real seconds.
const limits = {
  entries: MAX_ENTRIES,
  totalBytes: MAX_TOTAL_BYTES,
  timeoutMs: REQUEST_TIMEOUT_MS,
};
export function __testConfigureLimits(patch: {
  entries?: number;
  totalBytes?: number;
  timeoutMs?: number;
}): void {
  if (patch.entries !== undefined) limits.entries = patch.entries;
  if (patch.totalBytes !== undefined) limits.totalBytes = patch.totalBytes;
  if (patch.timeoutMs !== undefined) limits.timeoutMs = patch.timeoutMs;
}

const _cache = new Map<string, string>();
const _sizes = new Map<string, number>();
let _totalBytes = 0;
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
const _stats = { hits: 0, misses: 0, fetches: 0, failures: 0, evictions: 0 };

export interface ImageStats {
  cached: number;
  inFlight: number;
  hits: number;
  misses: number;
  fetches: number;
  failures: number;
  evictions: number;
  totalBytes: number;
}

export function getImageStats(): ImageStats {
  return {
    cached: _cache.size,
    inFlight: _inflight.size,
    hits: _stats.hits,
    misses: _stats.misses,
    fetches: _stats.fetches,
    failures: _stats.failures,
    evictions: _stats.evictions,
    totalBytes: _totalBytes,
  };
}

// Logout path. Bumps the generation so every in-flight load becomes a
// discarded result, then drops all cached bytes.
export function clearImageCache(): void {
  _generation += 1;
  _cache.clear();
  _sizes.clear();
  _totalBytes = 0;
}

// Test-only: reset every module-level counter/map between tests.
export function __testReset(): void {
  _cache.clear();
  _sizes.clear();
  _inflight.clear();
  _totalBytes = 0;
  _generation += 1;
  _active = 0;
  _waiters.length = 0;
  _stats.hits = 0;
  _stats.misses = 0;
  _stats.fetches = 0;
  _stats.failures = 0;
  _stats.evictions = 0;
}

function sleep(ms: number): Promise<void> {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

// Blob → data URI. React Native ships FileReader; plain Node (the unit-test
// runtime) does not, so the Node path converts via arrayBuffer+base64. The
// produced URI is byte-equivalent for the same payload.
async function blobToDataUri(blob: Blob): Promise<string> {
  const FR = (globalThis as { FileReader?: new () => FileReaderLike }).FileReader;
  if (FR !== undefined) {
    const reader = new FR();
    return await new Promise<string>((resolve, reject) => {
      reader.onloadend = () => resolve(reader.result as string);
      reader.onerror = () => reject(new Error('Failed to read image bytes'));
      reader.readAsDataURL(blob);
    });
  }
  const buf = await blob.arrayBuffer();
  let binary = '';
  const view = new Uint8Array(buf);
  for (let i = 0; i < view.length; i += 0x8000) {
    binary += String.fromCharCode(...view.subarray(i, i + 0x8000));
  }
  return `data:image/jpeg;base64,${btoa(binary)}`;
}

interface FileReaderLike {
  result: string | ArrayBuffer | null;
  onloadend: (() => void) | null;
  onerror: (() => void) | null;
  readAsDataURL(blob: Blob): void;
}

async function fetchBytes(path: string): Promise<{ uri: string; bytes: number }> {
  await acquireSlot();
  const controller = new AbortController();
  const timer = setTimeout(
    () => controller.abort(new Error(`Image request timed out after ${limits.timeoutMs}ms`)),
    limits.timeoutMs,
  );
  try {
    // Snapshot the source ONCE per attempt: a mid-flight logout must not let
    // a new session's cookie leak into this load's request.
    const src = authenticatedSource(path);
    if (!src) throw new ApiError(401, `GET ${path} → 401 (no session cookie)`);
    const res = await fetch(src.uri, {
      method: 'GET',
      headers: src.headers,
      credentials: 'include',
      signal: controller.signal,
    });
    if (!res.ok) {
      throw new ApiError(res.status, `GET ${path} → ${res.status}`);
    }
    const blob = await res.blob();
    const uri = await blobToDataUri(blob);
    // Blob.size is the wire size; the data-URI inflates it ~4/3. Track the
    // inflated footprint since that is what the JS heap actually holds.
    const wireBytes = Number.isFinite(blob.size) ? blob.size : 0;
    return { uri, bytes: Math.round((wireBytes * 4) / 3) };
  } finally {
    clearTimeout(timer);
    releaseSlot();
  }
}

function isTransient(err: unknown): boolean {
  if (err instanceof ApiError) return err.status >= 500 || err.status === 429;
  // Fetch network failures surface as TypeError in RN/Hermes. A request that
  // died from OUR timeout abort is transient too (retry on a fresh attempt).
  return !(err instanceof ApiError);
}

async function fetchWithRetry(
  path: string,
): Promise<{ uri: string; bytes: number }> {
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
      const { uri, bytes } = await fetchWithRetry(path);
      if (generation === _generation) {
        _cache.set(path, uri);
        _sizes.set(path, bytes);
        _totalBytes += bytes;
        // Bounded LRU: evict oldest-first until BOTH ceilings hold.
        while (_cache.size > limits.entries || _totalBytes > limits.totalBytes) {
          const oldest = _cache.keys().next().value;
          if (oldest === undefined) break;
          _cache.delete(oldest);
          const freed = _sizes.get(oldest) ?? 0;
          _sizes.delete(oldest);
          _totalBytes -= freed;
          _stats.evictions += 1;
          if (_cache.size === 0) break; // a single oversized entry may exceed alone
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

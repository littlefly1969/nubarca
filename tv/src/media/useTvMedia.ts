import { useEffect, useState } from 'react';
import { loadTvMedia, type TvMediaPriority } from '../api/client';
import { tvDebug } from '../debug';

// Shared hook for loading a derived TV media file (thumbnail / preview / poster)
// into a local file:// URI. Used by the grid thumbnail component and the
// slideshow image component so the download/cache/concurrency policy lives in one
// place. Never touches original full-resolution media.
export type TvMediaState = 'loading' | 'ready' | 'failed';

// The thumbnail→preview fallback is deliberately CONSERVATIVE: it only starts
// after the primary really failed, after a short defer (so it never competes
// with the initial burst of visible thumbnails), and strictly ONE fallback
// download at a time (previews are much heavier than thumbnails — an eager
// fallback fan-out is what made the grid feel slow).
const FALLBACK_DEFER_MS = 400;
let _fallbackBusy = false;
const _fallbackQueue: Array<() => void> = [];

async function withFallbackGate<T>(fn: () => Promise<T>): Promise<T> {
  if (_fallbackBusy) {
    await new Promise<void>((resolve) => { _fallbackQueue.push(resolve); });
  }
  _fallbackBusy = true;
  try {
    return await fn();
  } finally {
    const next = _fallbackQueue.shift();
    if (next) next(); // hand the gate to the next waiter
    else _fallbackBusy = false;
  }
}

interface UseTvMediaOptions {
  // Optional DERIVED fallback (e.g. the preview) tried once if the primary path
  // fails. Must still be an /api/tv/media path — resolveTvMediaUrl enforces
  // that; it is NEVER an original.
  fallbackPath?: string | null;
  // 'high' for media the user is looking at; 'low' for warm-up work.
  priority?: TvMediaPriority;
  // Personal Gallery media: downloads also carry the unlock grant header.
  personal?: boolean;
}

interface TvMediaResult {
  uri: string | null;
  state: TvMediaState;
  // Let a consumer demote a decoded-but-broken <Image> to the failed state.
  markFailed: () => void;
}

export function useTvMedia(
  path: string | null,
  { fallbackPath, priority = 'high', personal = false }: UseTvMediaOptions = {},
): TvMediaResult {
  const [uri, setUri] = useState<string | null>(null);
  const [state, setState] = useState<TvMediaState>('loading');

  useEffect(() => {
    let cancelled = false;
    const shouldAbort = () => cancelled;
    setUri(null);
    setState('loading');
    if (!path) {
      setState('failed');
      return;
    }
    const run = async () => {
      try {
        const primary = await loadTvMedia(path, { priority, shouldAbort, personal });
        if (!cancelled) {
          setUri(primary);
          setState('ready');
        }
        return;
      } catch {
        if (cancelled) return;
        // Primary genuinely failed. Try the derived fallback once — deferred,
        // low-priority, and serialized so it can never block the grid.
        if (fallbackPath && fallbackPath !== path) {
          await new Promise<void>((resolve) => { setTimeout(resolve, FALLBACK_DEFER_MS); });
          if (cancelled) return;
          try {
            const secondary = await withFallbackGate(() =>
              loadTvMedia(fallbackPath, { priority: 'low', shouldAbort, personal }));
            if (!cancelled) {
              tvDebug('media', 'fallback-used');
              setUri(secondary);
              setState('ready');
            }
            return;
          } catch {
            // fall through to failed
          }
        }
        if (!cancelled) setState('failed');
      }
    };
    void run();
    return () => {
      cancelled = true;
    };
  }, [path, fallbackPath, priority, personal]);

  return { uri, state, markFailed: () => setState('failed') };
}

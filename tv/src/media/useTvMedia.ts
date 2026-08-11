import { useCallback, useEffect, useRef, useState } from 'react';
import {
  loadTvMediaLeased,
  type TvMediaLease,
  type TvMediaPriority,
} from '../api/client';
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
  lease: TvMediaLease | null;
  // Let a consumer demote a decoded-but-broken <Image> to the failed state.
  markFailed: () => void;
}

interface ActiveLease {
  sourceKey: string;
  lease: TvMediaLease;
}

export function useTvMedia(
  path: string | null,
  { fallbackPath, priority = 'high', personal = false }: UseTvMediaOptions = {},
): TvMediaResult {
  const [uri, setUri] = useState<string | null>(null);
  const [state, setState] = useState<TvMediaState>('loading');
  const [active, setActive] = useState<ActiveLease | null>(null);
  const [decodeRetry, setDecodeRetry] = useState(0);
  const priorityRef = useRef(priority);
  priorityRef.current = priority;
  const leaseRef = useRef<TvMediaLease | null>(null);
  const retriedDecode = useRef(false);
  const sourceKey = `${path ?? ''}|${fallbackPath ?? ''}|${personal ? '1' : '0'}`;
  const sourceKeyRef = useRef(sourceKey);
  if (sourceKeyRef.current !== sourceKey) {
    sourceKeyRef.current = sourceKey;
    retriedDecode.current = false;
  }

  const replaceLease = useCallback((next: ActiveLease | null) => {
    const nextLease = next?.lease ?? null;
    if (leaseRef.current === nextLease) return;
    leaseRef.current?.release();
    leaseRef.current = nextLease;
    setActive(next);
  }, []);

  useEffect(() => () => {
    leaseRef.current?.release();
    leaseRef.current = null;
  }, []);

  useEffect(() => {
    let cancelled = false;
    const loadSourceKey = sourceKey;
    replaceLease(null);
    setUri(null);
    setState('loading');
    if (!path) {
      setState('failed');
      return;
    }
    const run = async () => {
      try {
        const primary = await loadTvMediaLeased(path, {
          priority: priorityRef.current,
          personal,
        });
        if (cancelled) {
          primary.release();
        } else {
          replaceLease({ sourceKey: loadSourceKey, lease: primary });
          setUri(primary.uri);
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
              cancelled
                ? Promise.resolve(null)
                : loadTvMediaLeased(fallbackPath, { priority: 'low', personal }));
            if (secondary !== null) {
              if (cancelled) {
                secondary.release();
              } else {
                tvDebug('media', 'fallback-used');
                replaceLease({ sourceKey: loadSourceKey, lease: secondary });
                setUri(secondary.uri);
                setState('ready');
              }
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
  }, [path, fallbackPath, personal, decodeRetry, replaceLease, sourceKey]);

  const currentLease = active?.sourceKey === sourceKey ? active.lease : null;

  const markFailed = useCallback(() => {
    // An onError from an Image belonging to the previous source must never
    // invalidate whichever path has become current since that Image mounted.
    if (!currentLease
      || leaseRef.current !== currentLease
      || active?.sourceKey !== sourceKeyRef.current) return;
    // With one owner this removes corrupt bytes before retrying. With shared
    // owners it deliberately leaves the file in place and retries only the
    // local decoder, so one view can never delete another view's live URI.
    currentLease.invalidate();
    replaceLease(null);
    setUri(null);
    if (!retriedDecode.current) {
      retriedDecode.current = true;
      setDecodeRetry((current) => current + 1);
    } else {
      setState('failed');
    }
  }, [active?.sourceKey, currentLease, replaceLease]);

  return {
    uri: currentLease ? uri : null,
    state: currentLease || state === 'failed' ? state : 'loading',
    lease: currentLease,
    markFailed,
  };
}

import { useCallback, useEffect, useRef } from 'react';
import { useLatest } from './hooks';

// ONE way the display polls anything — the control plane, the photographs, the
// greetings, the challenge hold, the face filter.
//
// SINGLE-FLIGHT, structurally: the next read is scheduled only when the last
// one has finished, so two requests for the same thing are never in the air
// and an old answer can never land on top of a newer one. A read asked for
// while one is in flight (a resume, a hint that the assignment moved) is not
// dropped and not run alongside: it runs the moment the current one returns,
// so what comes back afterwards is newer than whatever prompted it.
//
// Every read carries a TIMEOUT. A request that never answers — a half-open
// socket after a sleep, a proxy holding the line — would otherwise hold the
// single flight for ever, and the display would silently stop listening.

export interface PollOptions<T> {
  readonly enabled: boolean;
  readonly intervalMs: number;
  read(signal: AbortSignal): Promise<T>;
  onValue(value: T): void;
  /** A failed read. Return 'stop' to end the loop (a 401 has nothing left to poll). */
  onError?(error: unknown, consecutiveFailures: number): 'stop' | void;
  /** How long to wait after `failures` consecutive failures. Defaults to the interval. */
  retryDelayMs?(failures: number): number;
  readonly timeoutMs?: number;
  /** A new value reads at once — a resume, or a surface asking for fresh data. */
  readonly refreshKey?: unknown;
}

export const DEFAULT_POLL_TIMEOUT_MS = 12_000;

export function usePoll<T>(options: PollOptions<T>): () => void {
  const { enabled, intervalMs, timeoutMs = DEFAULT_POLL_TIMEOUT_MS, refreshKey } = options;
  const latest = useLatest(options);
  const refreshRef = useRef<() => void>(() => {});

  useEffect(() => {
    if (!enabled) {
      refreshRef.current = () => {};
      return;
    }
    let cancelled = false;
    let stopped = false;
    let inFlight = false;
    let again = false;
    let failures = 0;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let controller: AbortController | null = null;

    const schedule = (ms: number) => {
      if (timer !== undefined) clearTimeout(timer);
      timer = setTimeout(read, ms);
    };

    function read() {
      if (cancelled || stopped) return;
      if (inFlight) {
        again = true;
        return;
      }
      if (timer !== undefined) clearTimeout(timer);
      inFlight = true;
      const current = new AbortController();
      controller = current;
      const deadline = setTimeout(() => current.abort(), timeoutMs);
      let result: Promise<T>;
      try {
        result = latest.current.read(current.signal);
      } catch (error) {
        result = Promise.reject(error);
      }
      result
        .then((value) => {
          if (cancelled) return;
          failures = 0;
          latest.current.onValue(value);
        })
        .catch((error: unknown) => {
          if (cancelled) return;
          failures += 1;
          if (latest.current.onError?.(error, failures) === 'stop') stopped = true;
        })
        .finally(() => {
          clearTimeout(deadline);
          inFlight = false;
          if (controller === current) controller = null;
          if (cancelled || stopped) return;
          if (again) {
            again = false;
            read();
            return;
          }
          schedule(failures === 0
            ? intervalMs
            : latest.current.retryDelayMs?.(failures) ?? intervalMs);
        });
    }

    refreshRef.current = read;
    read();
    return () => {
      cancelled = true;
      if (timer !== undefined) clearTimeout(timer);
      controller?.abort();
      refreshRef.current = () => {};
    };
  }, [enabled, intervalMs, timeoutMs, latest]);

  // A new refresh key (a resume) reads now; the first value is the mount, which
  // the loop above has already read.
  const firstKey = useRef(true);
  useEffect(() => {
    if (firstKey.current) {
      firstKey.current = false;
      return;
    }
    refreshRef.current();
  }, [refreshKey]);

  return useCallback(() => refreshRef.current(), []);
}

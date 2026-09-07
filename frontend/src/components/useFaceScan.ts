import { useCallback, useEffect, useRef, useState } from 'react';

/* The scan a guest watches while their face is being searched for.
 *
 * The point of it is not to report progress — there is no progress to report,
 * the server either has an answer or does not — but to make the wait legible
 * and to let the guest SEE that the face was found. So it is deliberately not a
 * percentage, and it has a MINIMUM: a backend that answers in 80ms would
 * otherwise flash the whole thing past before anyone could read it, which reads
 * as a glitch rather than as a machine looking at your face.
 *
 * The result therefore waits on two conditions, not one: the answer has arrived
 * AND the scan has completed its passes. Whichever is slower decides.
 *
 * The minimum applies to EVERYONE, including under `prefers-reduced-motion`.
 * That setting asks for less movement, not for a different sequence, and the
 * guest still watches the tile find their face and hold on it — which is the
 * part worth seeing. Reduced motion stills the sweep; it does not skip ahead.
 */

/** One full sweep of the line, top to bottom. Matches the CSS duration. */
export const SCAN_PASS_MS = 900;

/** Sweeps to complete before a result may be shown. */
export const SCAN_MIN_PASSES = 3;

export interface FaceScan<T> {
  /** True while the scan is running — drives the animation, nothing else. */
  scanning: boolean;
  /** Begin a scan. Any scan already running is discarded. */
  begin: () => void;
  /** The answer arrived; released once the passes are done (or at once). */
  settle: (value: T) => void;
  /** Selfie changed, cancelled, closed: forget everything in flight. */
  reset: () => void;
}

/**
 * Runs the minimum-scan gate and hands `onSettled` the value once BOTH
 * conditions hold.
 *
 * Every timer is owned here and cleared on reset and on unmount, so a guest who
 * retakes their selfie or closes the sheet mid-scan leaves nothing behind that
 * could fire later and show them the answer to a question they withdrew.
 */
export function useFaceScan<T>(onSettled: (value: T) => void): FaceScan<T> {
  const [scanning, setScanning] = useState(false);
  const timerRef = useRef<number | null>(null);
  const passesDoneRef = useRef(false);
  const pendingRef = useRef<{ value: T } | null>(null);
  // The callback is read at fire time, so a re-render never leaves a stale one
  // holding the result.
  const settledRef = useRef(onSettled);
  settledRef.current = onSettled;

  const clearTimer = useCallback(() => {
    if (timerRef.current !== null) {
      window.clearTimeout(timerRef.current);
      timerRef.current = null;
    }
  }, []);

  const release = useCallback(() => {
    const pending = pendingRef.current;
    if (!pending || !passesDoneRef.current) return;
    pendingRef.current = null;
    setScanning(false);
    settledRef.current(pending.value);
  }, []);

  const begin = useCallback(() => {
    clearTimer();
    pendingRef.current = null;
    setScanning(true);
    passesDoneRef.current = false;
    timerRef.current = window.setTimeout(() => {
      timerRef.current = null;
      passesDoneRef.current = true;
      release();
    }, SCAN_PASS_MS * SCAN_MIN_PASSES);
  }, [clearTimer, release]);

  const settle = useCallback((value: T) => {
    pendingRef.current = { value };
    release();
  }, [release]);

  const reset = useCallback(() => {
    clearTimer();
    pendingRef.current = null;
    passesDoneRef.current = false;
    setScanning(false);
  }, [clearTimer]);

  useEffect(() => () => clearTimer(), [clearTimer]);

  return { scanning, begin, settle, reset };
}

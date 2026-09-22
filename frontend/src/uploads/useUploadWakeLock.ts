import { useCallback, useEffect, useRef } from 'react';

// KEEPING THE PHONE AWAKE FOR ONE UPLOAD, shared by every surface that sends
// files from somebody else's phone — the party's contribution page and an
// album's share link.
//
// Extracted rather than copied. Two implementations of "hold a wake lock while
// an upload runs" would drift, and the half that drifted would be the one
// nobody was looking at while a guest's screen went dark mid-video.
//
// It is BEST EFFORT by construction: a browser that has no Wake Lock API, or
// refuses one, changes nothing about the upload. What it cannot do is keep
// working after the tab is closed — that needs Background Fetch, which only
// Chrome on Android has — so no caller should promise that.

// Best-effort Screen Wake Lock for one foreground upload. The initial request
// is made directly from the upload click (important on WebKit), then reacquired
// only when an in-flight upload returns to a visible tab. Unsupported/denied
// locks never affect the upload itself.
export function useUploadWakeLock() {
  const wantedRef = useRef(false);
  const lockRef = useRef<WakeLockSentinel | null>(null);
  const pendingRef = useRef<Promise<void> | null>(null);

  const releaseCurrent = useCallback(() => {
    const lock = lockRef.current;
    lockRef.current = null;
    if (lock && !lock.released) void lock.release().catch(() => { /* best effort */ });
  }, []);

  const request = useCallback(() => {
    if (!wantedRef.current || document.visibilityState !== 'visible'
      || lockRef.current !== null || pendingRef.current !== null
      || !('wakeLock' in navigator)) return;

    try {
      let pending: Promise<void>;
      pending = navigator.wakeLock.request('screen')
        .then(async (lock) => {
          if (!wantedRef.current || document.visibilityState !== 'visible') {
            await lock.release().catch(() => { /* upload already stopped/hidden */ });
            return;
          }
          lockRef.current = lock;
          lock.addEventListener('release', () => {
            if (lockRef.current === lock) lockRef.current = null;
          }, { once: true });
        })
        .catch(() => { /* unsupported by policy/system: keep uploading */ })
        .finally(() => {
          if (pendingRef.current === pending) pendingRef.current = null;
        });
      pendingRef.current = pending;
    } catch {
      // A synchronous browser rejection is also non-fatal to the upload.
    }
  }, []);

  const start = useCallback(() => {
    wantedRef.current = true;
    request();
  }, [request]);

  const stop = useCallback(() => {
    wantedRef.current = false;
    releaseCurrent();
  }, [releaseCurrent]);

  useEffect(() => {
    const onVisibilityChange = () => {
      if (document.visibilityState === 'visible') {
        // If the hidden document still has a request settling, retry exactly
        // after it clears its pending guard. A successful old request makes the
        // retry a no-op; a rejected one no longer loses this visible transition.
        const pending = pendingRef.current;
        if (pending) void pending.then(request);
        else request();
      } else releaseCurrent();
    };
    document.addEventListener('visibilitychange', onVisibilityChange);
    return () => {
      document.removeEventListener('visibilitychange', onVisibilityChange);
      wantedRef.current = false;
      releaseCurrent();
    };
  }, [releaseCurrent, request]);

  return { start, stop };
}

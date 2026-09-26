import { useCallback, useEffect, useRef, useState } from 'react';
import { tvLog } from '../diagnostics';
import { useDisplayPlatform, type ResumeReason, type WakeLockHandle } from './displayPlatform';

/** The latest value, for callbacks that must stay stable across renders. */
export function useLatest<T>(value: T): { readonly current: T } {
  const ref = useRef(value);
  ref.current = value;
  return ref;
}

/** Is the page on screen? A hidden tab is not a display anybody is watching. */
export function usePageVisible(): boolean {
  const platform = useDisplayPlatform();
  const [visible, setVisible] = useState(() => platform.isVisible());
  useEffect(() => platform.onLifecycle((signal) => {
    if (signal === 'hidden') setVisible(false);
    else if (signal === 'visible' || signal === 'pageshow') setVisible(platform.isVisible());
  }), [platform]);
  return visible;
}

/** How often a missing wake lock is asked for again while the display is visible. */
export const WAKE_LOCK_RETRY_MS = 15_000;

/**
 * HOLD THE SCREEN while the display is doing its job.
 *
 * A browser releases a screen wake lock by itself whenever the page is hidden,
 * and the operating system may drop it across a sleep. So a lock is not a thing
 * acquired once: it is re-asked for on every return to the screen, and
 * re-checked on a slow interval so a lock the platform took away without an
 * event is recovered too. Best effort from end to end — a browser without the
 * API, or one that refuses, leaves a display that works and simply may dim.
 */
export function useWakeLock(active: boolean): void {
  const platform = useDisplayPlatform();
  useEffect(() => {
    if (!active) return;
    let disposed = false;
    let pending = false;
    let unsupported = false;
    let handle: WakeLockHandle | null = null;

    const acquire = async () => {
      if (disposed || unsupported || pending || handle !== null || !platform.isVisible()) return;
      pending = true;
      try {
        const next = await platform.requestWakeLock();
        if (disposed) {
          void next?.release().catch(() => {});
          return;
        }
        if (next === null) {
          unsupported = true;
          tvLog('tv.wakelock.failed', { reason: 'unsupported' });
          return;
        }
        handle = next;
        tvLog('tv.wakelock.acquired');
        next.onRelease(() => {
          if (handle !== next) return;
          handle = null;
          tvLog('tv.wakelock.released');
        });
      } catch (error) {
        tvLog('tv.wakelock.failed', { reason: error instanceof Error ? error.name : 'unknown' });
      } finally {
        pending = false;
      }
    };

    void acquire();
    const off = platform.onLifecycle((signal) => {
      if (signal !== 'hidden' && signal !== 'offline') void acquire();
    });
    const timer = window.setInterval(() => { void acquire(); }, WAKE_LOCK_RETRY_MS);
    return () => {
      disposed = true;
      off();
      window.clearInterval(timer);
      const held = handle;
      handle = null;
      if (held) void held.release().catch(() => {});
    };
  }, [active, platform]);
}

export const RESUME_TICK_MS = 1_000;
/**
 * A tick arriving this much later than it should is not a slow tick: the
 * machine slept, the display was off, or the browser froze the page.
 */
export const DISCONTINUITY_MS = 15_000;
/** Several signals for one wake-up (visible, pageshow, focus, a late tick) are one resume. */
const RESUME_DEBOUNCE_MS = 1_000;

/**
 * Tell the display it has just COME BACK — from a sleep, a switched-off screen,
 * a background tab or a frozen page — so it asks the server where it stands at
 * once instead of showing a pre-standby frame until the next interval.
 *
 * The browser has no single "resumed" event, so several signals are read, and
 * the one that always works is the clock: a one-second tick that arrives a
 * quarter of a minute late means the timers were frozen.
 */
export function useResume(onResume: (reason: ResumeReason) => void): void {
  const platform = useDisplayPlatform();
  const callback = useLatest(onResume);
  useEffect(() => {
    let last = platform.now();
    let lastFired = Number.NEGATIVE_INFINITY;
    const fire = (reason: ResumeReason) => {
      const now = platform.now();
      if (now - lastFired < RESUME_DEBOUNCE_MS) return;
      lastFired = now;
      tvLog('tv.resume', { reason });
      callback.current(reason);
    };
    const off = platform.onLifecycle((signal) => {
      if (signal === 'hidden' || signal === 'offline') return;
      if (signal === 'visible' && !platform.isVisible()) return;
      fire(signal);
    });
    const timer = window.setInterval(() => {
      const now = platform.now();
      const gap = now - last;
      last = now;
      if (gap > RESUME_TICK_MS + DISCONTINUITY_MS && platform.isVisible()) fire('discontinuity');
    }, RESUME_TICK_MS);
    return () => {
      off();
      window.clearInterval(timer);
    };
  }, [platform, callback]);
}

/**
 * True after `ms` without a pointer or a key. It hides the cursor and the
 * little controls a person might use, so a party wall does not look like a web
 * page with a mouse pointer parked in the middle of somebody's face.
 */
export function useIdle(ms: number): boolean {
  const [idle, setIdle] = useState(false);
  useEffect(() => {
    let timer = 0;
    const wake = () => {
      setIdle(false);
      window.clearTimeout(timer);
      timer = window.setTimeout(() => setIdle(true), ms);
    };
    wake();
    const passive: AddEventListenerOptions = { passive: true };
    window.addEventListener('mousemove', wake, passive);
    window.addEventListener('pointerdown', wake, passive);
    window.addEventListener('wheel', wake, passive);
    window.addEventListener('keydown', wake);
    return () => {
      window.clearTimeout(timer);
      window.removeEventListener('mousemove', wake);
      window.removeEventListener('pointerdown', wake);
      window.removeEventListener('wheel', wake);
      window.removeEventListener('keydown', wake);
    };
  }, [ms]);
  return idle;
}

/** Fullscreen, where the browser allows it — never a condition for the display to work. */
export function useFullscreen(): {
  supported: boolean;
  active: boolean;
  enter: () => void;
  exit: () => void;
} {
  const platform = useDisplayPlatform();
  const [active, setActive] = useState(() => platform.isFullscreen());
  useEffect(() => platform.onFullscreenChange(() => setActive(platform.isFullscreen())), [platform]);
  const enter = useCallback(() => {
    void platform.enterFullscreen().then((ok) => {
      if (!ok) tvLog('tv.fullscreen.failed');
      setActive(platform.isFullscreen());
    });
  }, [platform]);
  const exit = useCallback(() => {
    void platform.exitFullscreen().then(() => setActive(platform.isFullscreen()));
  }, [platform]);
  return { supported: platform.fullscreenSupported(), active, enter, exit };
}

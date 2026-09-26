import { createContext, useContext } from 'react';

// WHAT IS REALLY DIFFERENT ABOUT A BROWSER.
//
// A browser running /tv is a NubArca display with the same rules as the app.
// What differs is the platform underneath, and ONLY that lives here: holding
// the screen awake, going fullscreen, noticing that the machine slept, and
// reading a remote. The display's state machine asks these questions and never
// asks "am I a browser?" — there is no `if (browser)` anywhere in the Party
// logic, and a test replaces this whole object with a fake.
//
// Everything here is BEST EFFORT and fails quietly: a browser without the
// Screen Wake Lock API, one that refuses fullscreen, one that never fires
// `pageshow` — none of them may blank the screen, lose the pairing or throw
// into React.

/** The remote, as the display understands it: five-way keys plus a few accelerators. */
export type TvKey =
  | 'up' | 'down' | 'left' | 'right'
  | 'select' | 'back'
  | 'playPause' | 'next' | 'prev' | 'menu';

export interface WakeLockHandle {
  release(): Promise<void>;
  /** The browser let go on its own (the page was hidden, the battery saver…). */
  onRelease(listener: () => void): void;
}

/** Why the display thinks it has just come back. */
export type ResumeReason = 'visible' | 'pageshow' | 'focus' | 'online' | 'discontinuity';

export interface DisplayPlatform {
  /** A screen wake lock, or null where the browser has none or refuses one. */
  requestWakeLock(): Promise<WakeLockHandle | null>;
  fullscreenSupported(): boolean;
  isFullscreen(): boolean;
  enterFullscreen(): Promise<boolean>;
  exitFullscreen(): Promise<void>;
  onFullscreenChange(listener: () => void): () => void;
  isVisible(): boolean;
  /** visibility, pageshow, focus, online/offline — the raw lifecycle signals. */
  onLifecycle(listener: (signal: ResumeReason | 'hidden' | 'offline') => void): () => void;
  now(): number;
  mapKey(event: Pick<KeyboardEvent, 'key'>): TvKey | null;
}

// A remote that a browser sees as a keyboard — HDMI-CEC, USB or Bluetooth —
// sends these names. Nothing here is Android- or Fire OS-specific.
const KEYS: Record<string, TvKey> = {
  ArrowUp: 'up',
  ArrowDown: 'down',
  ArrowLeft: 'left',
  ArrowRight: 'right',
  Enter: 'select',
  ' ': 'select',
  Select: 'select',
  Escape: 'back',
  Backspace: 'back',
  BrowserBack: 'back',
  GoBack: 'back',
  MediaPlayPause: 'playPause',
  MediaPlay: 'playPause',
  MediaPause: 'playPause',
  MediaTrackNext: 'next',
  MediaFastForward: 'next',
  MediaTrackPrevious: 'prev',
  MediaRewind: 'prev',
  ContextMenu: 'menu',
};

export function mapTvKey(event: Pick<KeyboardEvent, 'key'>): TvKey | null {
  return KEYS[event.key] ?? null;
}

interface WakeLockSentinelLike {
  release(): Promise<void>;
  addEventListener(type: 'release', listener: () => void): void;
}

interface WakeLockApi {
  request(type: 'screen'): Promise<WakeLockSentinelLike>;
}

export const browserPlatform: DisplayPlatform = {
  async requestWakeLock() {
    const api = (navigator as unknown as { wakeLock?: WakeLockApi }).wakeLock;
    if (!api) return null;
    const sentinel = await api.request('screen');
    return {
      release: () => sentinel.release(),
      onRelease: (listener) => sentinel.addEventListener('release', listener),
    };
  },
  fullscreenSupported: () => typeof document.documentElement.requestFullscreen === 'function',
  isFullscreen: () => document.fullscreenElement !== null && document.fullscreenElement !== undefined,
  async enterFullscreen() {
    try {
      await document.documentElement.requestFullscreen();
      return true;
    } catch {
      return false;
    }
  },
  async exitFullscreen() {
    if (!document.fullscreenElement) return;
    try {
      await document.exitFullscreen();
    } catch {
      /* already out, or refused: the display works either way */
    }
  },
  onFullscreenChange(listener) {
    document.addEventListener('fullscreenchange', listener);
    return () => document.removeEventListener('fullscreenchange', listener);
  },
  isVisible: () => document.visibilityState !== 'hidden',
  onLifecycle(listener) {
    const visibility = () => listener(document.visibilityState === 'hidden' ? 'hidden' : 'visible');
    const pageshow = () => listener('pageshow');
    const focus = () => listener('focus');
    const online = () => listener('online');
    const offline = () => listener('offline');
    document.addEventListener('visibilitychange', visibility);
    window.addEventListener('pageshow', pageshow);
    window.addEventListener('focus', focus);
    window.addEventListener('online', online);
    window.addEventListener('offline', offline);
    return () => {
      document.removeEventListener('visibilitychange', visibility);
      window.removeEventListener('pageshow', pageshow);
      window.removeEventListener('focus', focus);
      window.removeEventListener('online', online);
      window.removeEventListener('offline', offline);
    };
  },
  now: () => Date.now(),
  mapKey: mapTvKey,
};

export const DisplayPlatformContext = createContext<DisplayPlatform>(browserPlatform);

export function useDisplayPlatform(): DisplayPlatform {
  return useContext(DisplayPlatformContext);
}

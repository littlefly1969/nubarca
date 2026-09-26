import type { DisplayPlatform, ResumeReason, TvKey, WakeLockHandle } from '../platform/displayPlatform';
import { mapTvKey } from '../platform/displayPlatform';

// A display platform a test can drive: the page hidden and shown, the machine
// "asleep" (the clock jumps), wake locks granted, refused or taken away, and
// fullscreen allowed or not.

export interface FakePlatform extends DisplayPlatform {
  visible: boolean;
  /** Milliseconds added to Date.now(): a jump is a sleep the timers never saw. */
  clockOffset: number;
  wakeLockRequests: number;
  wakeLocksHeld: number;
  wakeLockMode: 'grant' | 'unsupported' | 'reject';
  fullscreen: boolean;
  fullscreenAllowed: boolean;
  emit(signal: ResumeReason | 'hidden' | 'offline'): void;
  hide(): void;
  show(): void;
  /** The browser takes every held lock away (a hidden page, a battery saver). */
  dropWakeLocks(): void;
}

export function createFakePlatform(): FakePlatform {
  const listeners = new Set<(signal: ResumeReason | 'hidden' | 'offline') => void>();
  const fullscreenListeners = new Set<() => void>();
  const held = new Set<{ released: boolean; onRelease: Array<() => void> }>();

  const platform: FakePlatform = {
    visible: true,
    clockOffset: 0,
    wakeLockRequests: 0,
    wakeLocksHeld: 0,
    wakeLockMode: 'grant',
    fullscreen: false,
    fullscreenAllowed: true,
    async requestWakeLock(): Promise<WakeLockHandle | null> {
      platform.wakeLockRequests += 1;
      if (platform.wakeLockMode === 'unsupported') return null;
      if (platform.wakeLockMode === 'reject') throw new DOMException('denied', 'NotAllowedError');
      const lock = { released: false, onRelease: [] as Array<() => void> };
      held.add(lock);
      platform.wakeLocksHeld = held.size;
      return {
        release: async () => {
          if (lock.released) return;
          lock.released = true;
          held.delete(lock);
          platform.wakeLocksHeld = held.size;
          lock.onRelease.forEach((listener) => listener());
        },
        onRelease: (listener) => { lock.onRelease.push(listener); },
      };
    },
    fullscreenSupported: () => true,
    isFullscreen: () => platform.fullscreen,
    async enterFullscreen() {
      if (!platform.fullscreenAllowed) return false;
      platform.fullscreen = true;
      fullscreenListeners.forEach((listener) => listener());
      return true;
    },
    async exitFullscreen() {
      platform.fullscreen = false;
      fullscreenListeners.forEach((listener) => listener());
    },
    onFullscreenChange(listener) {
      fullscreenListeners.add(listener);
      return () => fullscreenListeners.delete(listener);
    },
    isVisible: () => platform.visible,
    onLifecycle(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    now: () => Date.now() + platform.clockOffset,
    mapKey: (event: Pick<KeyboardEvent, 'key'>): TvKey | null => mapTvKey(event),
    emit(signal) {
      listeners.forEach((listener) => listener(signal));
    },
    hide() {
      platform.visible = false;
      platform.dropWakeLocks();
      platform.emit('hidden');
    },
    show() {
      platform.visible = true;
      platform.emit('visible');
    },
    dropWakeLocks() {
      for (const lock of [...held]) {
        lock.released = true;
        held.delete(lock);
        lock.onRelease.forEach((listener) => listener());
      }
      platform.wakeLocksHeld = held.size;
    },
  };
  return platform;
}

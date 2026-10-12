import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, useSyncExternalStore } from 'react';
import type { ReactNode } from 'react';
import { useAuth } from '../auth/useAuth';
import { useUploadWakeLock } from './useUploadWakeLock';
import { isFinished, UploadManager, type UploadEntry, type UploadItem, type UploadSnapshot } from './engine/uploadManager';
import type { UploadTarget } from './engine/targets';
import { UploadTray } from './UploadTray';

// THE UPLOAD QUEUE LIVES WITH THE SHELL, not with a page. Mounted once inside
// the authenticated layout, so an upload started in a folder keeps going — and
// keeps showing in the tray — while the person moves to the library, an album
// or anywhere else in the app.

interface UploadContextValue {
  manager: UploadManager;
  /** Queues files for a destination and opens the tray. Call from the user's gesture. */
  enqueue(entries: readonly UploadEntry[], target: UploadTarget): void;
  trayOpen: boolean;
  setTrayOpen(open: boolean): void;
}

const UploadContext = createContext<UploadContextValue | null>(null);

export function UploadProvider({ children, manager: injected }: { children: ReactNode; manager?: UploadManager }) {
  const { invalidateAuth } = useAuth();
  const [manager] = useState(() => injected ?? new UploadManager());
  const [trayOpen, setTrayOpen] = useState(false);
  const { start: startWakeLock, stop: stopWakeLock } = useUploadWakeLock();
  const snapshot = useSyncExternalStore(manager.subscribe, manager.getSnapshot);
  const active = snapshot.items.some((item) => !isFinished(item.phase) && item.phase !== 'paused');

  const invalidateRef = useRef(invalidateAuth);
  invalidateRef.current = invalidateAuth;
  useEffect(() => {
    manager.onAuthLost = () => invalidateRef.current();
    return () => { manager.onAuthLost = undefined; };
  }, [manager]);

  // The engine outlives pages, not the shell: signing out (the shell unmounts)
  // stops whatever is still moving.
  useEffect(() => () => manager.dispose(), [manager]);

  // Keep the screen on while bytes move, and warn before a tab close would
  // drop the files still queued (a reload can resume, but only if the person
  // picks them again).
  useEffect(() => {
    if (!active) {
      stopWakeLock();
      return undefined;
    }
    startWakeLock();
    const warn = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Legacy browsers need a value; the text itself is never shown.
      event.returnValue = '';
    };
    window.addEventListener('beforeunload', warn);
    return () => window.removeEventListener('beforeunload', warn);
  }, [active, startWakeLock, stopWakeLock]);

  const enqueue = useCallback((entries: readonly UploadEntry[], target: UploadTarget) => {
    if (entries.length === 0) return;
    // Requested inside the gesture: WebKit only grants a wake lock there.
    startWakeLock();
    manager.enqueue(entries, target);
    setTrayOpen(true);
  }, [manager, startWakeLock]);

  const value = useMemo<UploadContextValue>(
    () => ({ manager, enqueue, trayOpen, setTrayOpen }),
    [manager, enqueue, trayOpen],
  );

  return (
    <UploadContext.Provider value={value}>
      {children}
      <UploadTray snapshot={snapshot} />
    </UploadContext.Provider>
  );
}

export function useUploads(): UploadContextValue {
  const value = useContext(UploadContext);
  if (!value) throw new Error('useUploads must be used inside <UploadProvider>.');
  return value;
}

/** The live queue. */
export function useUploadSnapshot(): UploadSnapshot {
  const { manager } = useUploads();
  return useSyncExternalStore(manager.subscribe, manager.getSnapshot);
}

/**
 * Calls `onLanded` (coalesced) whenever a file arrives at a destination that
 * `matches` — how a folder view knows to reload without polling.
 */
export function useUploadLanded(matches: (target: UploadTarget) => boolean, onLanded: () => void, delayMs = 600) {
  const { manager } = useUploads();
  const matchesRef = useRef(matches);
  matchesRef.current = matches;
  const landedRef = useRef(onLanded);
  landedRef.current = onLanded;
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout> | null = null;
    const unsubscribe = manager.onCompleted((item: UploadItem) => {
      if (!matchesRef.current(item.target)) return;
      if (timer !== null) return;
      timer = setTimeout(() => {
        timer = null;
        landedRef.current();
      }, delayMs);
    });
    return () => {
      unsubscribe();
      if (timer !== null) clearTimeout(timer);
    };
  }, [manager, delayMs]);
}

/**
 * Calls `onSettled` once the files queued for a destination that `matches`
 * have all finished and at least one of them arrived — for views that a
 * reload disturbs (a media wall), which would rather refresh once per batch
 * than once per file. A very long batch still refreshes every `maxWaitMs`.
 */
export function useUploadsSettled(matches: (target: UploadTarget) => boolean, onSettled: () => void, maxWaitMs = 20_000) {
  const { manager } = useUploads();
  const matchesRef = useRef(matches);
  matchesRef.current = matches;
  const settledRef = useRef(onSettled);
  settledRef.current = onSettled;
  useEffect(() => {
    let landedSince: number | null = null;
    const fire = () => {
      landedSince = null;
      settledRef.current();
    };
    const check = () => {
      if (landedSince === null) return;
      const busy = manager.getSnapshot().items.some(
        (item) => matchesRef.current(item.target) && !isFinished(item.phase) && item.phase !== 'paused',
      );
      if (!busy || Date.now() - landedSince >= maxWaitMs) fire();
    };
    const offCompleted = manager.onCompleted((item: UploadItem) => {
      if (!matchesRef.current(item.target)) return;
      if (landedSince === null) landedSince = Date.now();
    });
    const offSnapshot = manager.subscribe(check);
    return () => {
      offCompleted();
      offSnapshot();
    };
  }, [manager, maxWaitMs]);
}

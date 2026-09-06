import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError, getPartyGamePublicSnapshot, joinPartyGame,
  type PartyGamePublicSnapshot,
} from '@nubarca/api-client';

// The guest's and the television's connection to the game, in one hook.
//
// Reconnection is not a feature here; it is the absence of one. There is no
// realtime transport in NubArca — every live surface polls a snapshot — so a
// refresh, a locked phone, a walk out of Wi-Fi range and a backgrounded tab all
// recover the same way: read the snapshot again. Nothing is replayed, nothing is
// reconciled, and no client holds state the server cannot reproduce.
//
// Two details make that true in practice rather than in principle.
//
// A hidden tab stops polling and refetches the moment it comes back, so a phone
// that spent the last activity in somebody's pocket shows the CURRENT scene
// immediately rather than the one it fell asleep on.
//
// A failed poll is not an error state. The party did not stop because one
// request did; the snapshot already on screen stays, and the next tick tries
// again. Only a request that has never succeeded produces a visible failure.

const POLL_MS = 2_500;

export type PartyGameConnection = 'loading' | 'ready' | 'unavailable' | 'error';

export interface PartyGameFeed {
  snapshot: PartyGamePublicSnapshot | null;
  connection: PartyGameConnection;
  /** True while the last attempt failed but a usable snapshot is still on screen. */
  stale: boolean;
  refresh(): void;
  /** Adopt a snapshot returned by a write (a vote), without waiting for a poll. */
  adopt(snapshot: PartyGamePublicSnapshot): void;
}

export interface PartyGameFeedOptions {
  /**
   * Whether to announce this client as a guest before reading.
   *
   * A phone joins: it becomes a participant, so it counts toward the room and
   * can vote. A television does NOT, because a display that mints a participant
   * inflates the very count it is showing.
   */
  join?: boolean;
  /**
   * Announce this client as a television. It stamps the party's display
   * heartbeat, which is the only honest source for the control room's "a screen
   * is showing the game" — a server guessing from the absence of a guest cookie
   * would count a guest who has not joined.
   */
  asDisplay?: boolean;
  pollMs?: number;
}

export function usePartyGameSnapshot(
  token: string | undefined,
  { join = false, asDisplay = false, pollMs = POLL_MS }: PartyGameFeedOptions = {},
): PartyGameFeed {
  const [snapshot, setSnapshot] = useState<PartyGamePublicSnapshot | null>(null);
  const [connection, setConnection] = useState<PartyGameConnection>('loading');
  const [stale, setStale] = useState(false);
  // The join is one deliberate act per mount, never a side effect of polling.
  const joined = useRef(false);
  const hasSnapshot = useRef(false);
  const [tick, setTick] = useState(0);

  const refresh = useCallback(() => setTick((n) => n + 1), []);
  const adopt = useCallback((next: PartyGamePublicSnapshot) => {
    hasSnapshot.current = true;
    setSnapshot(next);
    setConnection('ready');
    setStale(false);
  }, []);

  useEffect(() => {
    joined.current = false;
    hasSnapshot.current = false;
    setSnapshot(null);
    setConnection('loading');
    setStale(false);
  }, [token]);

  useEffect(() => {
    if (!token) { setConnection('unavailable'); return; }
    let cancelled = false;
    const controller = new AbortController();

    const read = async () => {
      try {
        const shouldJoin = join && !joined.current;
        const next = shouldJoin
          ? await joinPartyGame(token, controller.signal)
          : await getPartyGamePublicSnapshot(token, controller.signal, asDisplay);
        if (cancelled) return;
        if (shouldJoin) joined.current = true;
        hasSnapshot.current = true;
        setSnapshot(next);
        setConnection('ready');
        setStale(false);
      } catch (error) {
        if (cancelled || (error instanceof DOMException && error.name === 'AbortError')) return;
        // A party that has no game is a different fact from a party we could not
        // reach, and only the first is permanent.
        if (error instanceof ApiError && error.status === 404) {
          setConnection('unavailable');
          return;
        }
        if (hasSnapshot.current) setStale(true);
        else setConnection('error');
      }
    };

    void read();
    let timer: ReturnType<typeof setInterval> | undefined;
    const start = () => {
      if (timer === undefined) timer = setInterval(() => void read(), pollMs);
    };
    const stop = () => {
      if (timer !== undefined) { clearInterval(timer); timer = undefined; }
    };

    const onVisibility = () => {
      if (document.visibilityState === 'hidden') { stop(); return; }
      // Back in the foreground: read immediately rather than waiting out a tick,
      // so a phone that was in a pocket shows the current scene at once.
      void read();
      start();
    };

    if (document.visibilityState !== 'hidden') start();
    document.addEventListener('visibilitychange', onVisibility);
    window.addEventListener('focus', onVisibility);
    window.addEventListener('online', onVisibility);

    return () => {
      cancelled = true;
      controller.abort();
      stop();
      document.removeEventListener('visibilitychange', onVisibility);
      window.removeEventListener('focus', onVisibility);
      window.removeEventListener('online', onVisibility);
    };
  }, [token, join, asDisplay, pollMs, tick]);

  return { snapshot, connection, stale, refresh, adopt };
}

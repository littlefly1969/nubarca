import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError, PartyGameConflict, getPartyGameSnapshot, sendPartyGameCommand,
  type PartyGameCommand, type PartyGameCommandCode, type PartyGameSnapshot,
} from '@nubarca/api-client';

// The owner's connection to their own game: read it, and move it.
//
// Two things make this safe to hold in front of a room.
//
// EVERY COMMAND QUOTES A VERSION, taken from the snapshot on screen. A refusal
// is not an error to show and forget: it carries the state it was measured
// against, so a second tab, a second device or a double tap ends with this
// screen CORRECT rather than merely told off — one advance, one re-render, no
// follow-up fetch to remember.
//
// NOTHING IS OPTIMISTIC. The phase on screen is always a phase the server has
// already committed to, because a control room that shows "voting open" before
// the server agrees is a control room that lies to a host who is about to speak.

const POLL_MS = 2_500;

export type ControlConnection = 'loading' | 'ready' | 'unavailable' | 'error';

export interface PartyGameControl {
  snapshot: PartyGameSnapshot | null;
  connection: ControlConnection;
  stale: boolean;
  /** The command in flight, so a surface can disable exactly one control. */
  pending: PartyGameCommand | null;
  /** Set when the last command was refused, cleared by the next success. */
  refusal: PartyGameCommandCode | null;
  run(command: PartyGameCommand): Promise<void>;
  refresh(): void;
}

export function usePartyGameControl(albumId: string | undefined): PartyGameControl {
  const [snapshot, setSnapshot] = useState<PartyGameSnapshot | null>(null);
  const [connection, setConnection] = useState<ControlConnection>('loading');
  const [stale, setStale] = useState(false);
  const [pending, setPending] = useState<PartyGameCommand | null>(null);
  const [refusal, setRefusal] = useState<PartyGameCommandCode | null>(null);
  const [tick, setTick] = useState(0);
  const hasSnapshot = useRef(false);
  // A poll landing while a command is in flight would put a pre-command
  // snapshot back on screen and re-arm a version the server has already spent.
  const inFlight = useRef(false);

  const refresh = useCallback(() => setTick((n) => n + 1), []);

  useEffect(() => {
    hasSnapshot.current = false;
    setSnapshot(null);
    setConnection('loading');
    setStale(false);
    setRefusal(null);
  }, [albumId]);

  useEffect(() => {
    if (!albumId) { setConnection('unavailable'); return; }
    let cancelled = false;
    const controller = new AbortController();

    const read = async () => {
      if (inFlight.current) return;
      try {
        const next = await getPartyGameSnapshot(albumId, controller.signal);
        if (cancelled || inFlight.current) return;
        hasSnapshot.current = true;
        setSnapshot(next);
        setConnection('ready');
        setStale(false);
      } catch (error) {
        if (cancelled || (error instanceof DOMException && error.name === 'AbortError')) return;
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
    const start = () => { if (timer === undefined) timer = setInterval(() => void read(), POLL_MS); };
    const stop = () => { if (timer !== undefined) { clearInterval(timer); timer = undefined; } };
    const onVisibility = () => {
      if (document.visibilityState === 'hidden') { stop(); return; }
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
  }, [albumId, tick]);

  const run = useCallback(async (command: PartyGameCommand) => {
    if (!albumId || !snapshot || pending) return;
    setPending(command);
    setRefusal(null);
    inFlight.current = true;
    try {
      setSnapshot(await sendPartyGameCommand(albumId, command, snapshot.version));
      setConnection('ready');
      setStale(false);
    } catch (error) {
      if (error instanceof PartyGameConflict) {
        // The refusal IS the recovery: adopt what the server says is true, and
        // name what happened so the host is not left guessing.
        if (error.snapshot) {
          setSnapshot(error.snapshot as PartyGameSnapshot);
          setConnection('ready');
          setStale(false);
        }
        setRefusal(error.code as PartyGameCommandCode);
      } else {
        setStale(true);
      }
    } finally {
      inFlight.current = false;
      setPending(null);
    }
  }, [albumId, snapshot, pending]);

  return { snapshot, connection, stale, pending, refusal, run, refresh };
}

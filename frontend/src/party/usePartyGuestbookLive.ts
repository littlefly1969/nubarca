import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  PartyGuestbookLiveConflict,
  type PartyGuestbookLiveCommand,
  type PartyGuestbookLiveControl,
  type PartyGuestbookLiveRefusalCode,
} from '@nubarca/api-client';
import { usePartyApi } from './workspace/partyApi';

// The regia's hold on the guest book during the evening: whether the room may
// read it, and whether it is on the television.
//
// The same two rules as the game's control, for the same reason — this is
// operated in front of a room:
//
// EVERY COMMAND QUOTES A VERSION, and a refusal carries the state it was
// measured against. The control adopts it, so a second phone in the crew or a
// game command from the other regia ends with this screen correct.
//
// NOTHING IS OPTIMISTIC. The switch shows what the server committed, and the
// television's line shows what the server says a paired television is told to
// show — which is not always what the regia asked for.

/**
 * How often the control re-reads. The television's presentation can change
 * without anybody touching THIS card — the game takes the screen, the last
 * memory is hidden — so it is polled, at the pace of the television itself.
 */
export const GUESTBOOK_LIVE_POLL_MS = 5_000;

export type GuestbookLiveConnection = 'loading' | 'ready' | 'unavailable' | 'error';

export interface PartyGuestbookLive {
  control: PartyGuestbookLiveControl | null;
  connection: GuestbookLiveConnection;
  stale: boolean;
  /** The command in flight, so exactly one control is disabled. */
  pending: PartyGuestbookLiveCommand | null;
  /** Set when the last command was refused, cleared by the next success. */
  refusal: PartyGuestbookLiveRefusalCode | 'failed' | null;
  run(command: PartyGuestbookLiveCommand): Promise<void>;
  refresh(): void;
}

export function usePartyGuestbookLive(albumId: string | null | undefined): PartyGuestbookLive {
  const api = usePartyApi();
  const [control, setControl] = useState<PartyGuestbookLiveControl | null>(null);
  const [connection, setConnection] = useState<GuestbookLiveConnection>('loading');
  const [stale, setStale] = useState(false);
  const [pending, setPending] = useState<PartyGuestbookLiveCommand | null>(null);
  const [refusal, setRefusal] = useState<PartyGuestbookLive['refusal']>(null);
  const [tick, setTick] = useState(0);
  const hasControl = useRef(false);
  // A poll landing while a command is in flight would put the pre-command
  // state back on screen and re-arm a version the server has already spent.
  const inFlight = useRef(false);

  const refresh = useCallback(() => setTick((n) => n + 1), []);

  useEffect(() => {
    hasControl.current = false;
    setControl(null);
    setConnection('loading');
    setStale(false);
    setRefusal(null);
  }, [albumId]);

  useEffect(() => {
    if (!albumId) { setConnection('unavailable'); return; }
    let cancelled = false;
    const controller = new AbortController();
    // Single-flight: a slow answer is never overtaken by the next tick.
    let reading = false;

    const read = async () => {
      if (inFlight.current || reading) return;
      reading = true;
      try {
        const next = await api.getPartyGuestbookLive(albumId, controller.signal);
        if (cancelled || inFlight.current) return;
        hasControl.current = true;
        setControl(next);
        setConnection('ready');
        setStale(false);
      } catch (error) {
        if (cancelled || (error instanceof DOMException && error.name === 'AbortError')) return;
        // No live party behind this album, or — for a Party Crew device — a
        // role that holds neither half of these controls.
        if (error instanceof ApiError && error.status === 404) {
          setConnection('unavailable');
          return;
        }
        if (hasControl.current) setStale(true);
        else setConnection('error');
      } finally {
        reading = false;
      }
    };

    void read();
    let timer: ReturnType<typeof setInterval> | undefined;
    const start = () => { if (timer === undefined) timer = setInterval(() => void read(), GUESTBOOK_LIVE_POLL_MS); };
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
  }, [albumId, tick, api]);

  const run = useCallback(async (command: PartyGuestbookLiveCommand) => {
    if (!albumId || !control || pending) return;
    setPending(command);
    setRefusal(null);
    inFlight.current = true;
    try {
      setControl(await api.sendPartyGuestbookLiveCommand(albumId, command, control.version));
      setConnection('ready');
      setStale(false);
    } catch (error) {
      if (error instanceof PartyGuestbookLiveConflict) {
        // The refusal IS the recovery: adopt what the server says is true,
        // and name what happened.
        if (error.control) {
          setControl(error.control);
          setConnection('ready');
          setStale(false);
        }
        setRefusal(error.code);
      } else {
        setRefusal('failed');
        setStale(true);
      }
    } finally {
      inFlight.current = false;
      setPending(null);
    }
  }, [albumId, control, pending, api]);

  return { control, connection, stale, pending, refusal, run, refresh };
}

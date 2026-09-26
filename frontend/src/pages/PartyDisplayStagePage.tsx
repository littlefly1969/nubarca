import { useCallback, useEffect, useLayoutEffect, useRef, useState } from 'react';
import {
  ApiError,
  fetchPartyDisplayMedia,
  getPartyDisplayJoinQr,
  getPartyDisplaySnapshot,
  type PartyGamePublicSnapshot,
} from '@nubarca/api-client';
import { PartyTvStage } from '../party/PartyTvStage';
import { stageScene } from '../party/stageScene';
import { displayRetryDelayMs, isRetryableDisplayFailure } from '../party/displayRetry';
import { usePoll } from '../tv/platform/usePoll';

// The party show as a PAIRED TELEVISION sees it, at /party-display/stage.
//
// Same canonical stage as the public route — same scenes, same card, same CSS.
// Everything different about this page is about how it is AUTHORISED, and that
// is the whole point of it existing separately: a television is a display, not
// a guest, so it never holds a party token, never acquires a browser cookie and
// never becomes a participant.
//
// THE GRANT LIVES IN MEMORY AND NOWHERE ELSE. It arrives in the URL fragment,
// which browsers do not send to servers and which therefore cannot reach an
// access log or a Referer header. The fragment is stripped from history on the
// first render, and the value is never written to localStorage, sessionStorage,
// a cookie or IndexedDB — so a reload has no credential, and asks the native
// shell for a new one rather than inventing anything.
//
// The native shell owns the lifecycle: it mints the grant, renews it, watches
// this page through the bridge below, covers it until it has drawn a real
// frame, and remounts the WebView when the renderer dies. This page owns the
// presentation and nothing else — and it tells the shell four things, each of
// which means exactly one thing:
//
//   display-heartbeat    this page's JavaScript is alive (every 2s)
//   display-ready        the first valid snapshot has been rendered
//   display-auth-failed  the grant was refused — the CAPABILITY is dead, not
//                        the renderer, which is why it is not merely silence
//   display-presentation whether a live scene of the party is on screen, for
//                        the keep-awake policy; coarse on purpose, so the shell
//                        never learns what a phase is
//
// None of them carries the grant, the snapshot or anything from the server.

const POLL_MS = 2_500;
/** A snapshot read that has not answered in this long is given up on and asked again. */
const SNAPSHOT_TIMEOUT_MS = 8_000;
// The renderer heartbeat the native shell watches. It measures THIS page being
// alive, not the party server: a wedged JS context still answers no beats.
const HEARTBEAT_MS = 2_000;
// The bridge protocol this page speaks. A shell seeing less treats the page as
// one that can only prove it is alive.
const BRIDGE_PROTOCOL = 2;

type ShellMessage =
  | { type: 'display-heartbeat'; protocol: number; at: number }
  | { type: 'display-ready' }
  | { type: 'display-auth-failed' }
  | { type: 'display-presentation'; active: boolean };

function postToShell(message: ShellMessage): void {
  const bridge = (window as unknown as {
    ReactNativeWebView?: { postMessage(data: string): void };
  }).ReactNativeWebView;
  try {
    bridge?.postMessage(JSON.stringify(message));
  } catch {
    // A shell that cannot hear us is the shell's problem to notice; the page
    // must not fall over because it is running in a plain browser.
  }
}

function isAbort(error: unknown): boolean {
  return error instanceof DOMException && error.name === 'AbortError';
}

function statusOf(error: unknown): number | null {
  return error instanceof ApiError ? error.status : null;
}

export function PartyDisplayStagePage() {
  const [grant] = useState(() => readGrantFromFragment());

  // The fragment is removed from the address bar and from history before
  // anything else runs. It stays only in the closure above.
  useEffect(() => {
    if (window.location.hash) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search);
    }
  }, []);

  // The renderer heartbeat. The shell treats silence as a dead page and
  // remounts, so this must keep running for as long as the document does. A
  // LAYOUT effect so the first beat still precedes anything the stage below
  // reports: its effects run before this component's passive ones.
  useLayoutEffect(() => {
    const beat = () => postToShell({ type: 'display-heartbeat', protocol: BRIDGE_PROTOCOL, at: Date.now() });
    beat();
    const timer = setInterval(beat, HEARTBEAT_MS);
    return () => clearInterval(timer);
  }, []);

  return (
    <PartyDisplayStage
      grant={grant}
      onReady={() => postToShell({ type: 'display-ready' })}
      onAuthFailed={() => postToShell({ type: 'display-auth-failed' })}
      onPresentation={(active) => postToShell({ type: 'display-presentation', active })}
    />
  );
}

export interface PartyDisplayStageProps {
  /** The display grant, or null when there is none to use. */
  readonly grant: string | null;
  /** The first valid snapshot has been rendered. */
  onReady?(): void;
  /** The grant was refused: the CAPABILITY is dead, not the renderer. */
  onAuthFailed?(): void;
  /** Whether a live scene of the party is on screen, for the keep-awake policy. */
  onPresentation?(active: boolean): void;
  /** A new value reads the snapshot at once (a display that just resumed). */
  readonly refreshKey?: unknown;
}

/**
 * THE CANONICAL GAME STAGE, authorised by a display grant.
 *
 * Rendered by this page inside the native app's WebView, and in-process by a
 * browser running /tv — the same stage, the same scenes, the same grant, so a
 * room is never given two accounts of what the game is doing. What differs is
 * only who is told about readiness and refusals: the page tells the native
 * shell through its bridge, the browser display is told directly.
 */
export function PartyDisplayStage({
  grant, onReady, onAuthFailed, onPresentation, refreshKey,
}: PartyDisplayStageProps) {
  const [snapshot, setSnapshot] = useState<PartyGamePublicSnapshot | null>(null);
  const [connection, setConnection] = useState<'loading' | 'ready' | 'unavailable' | 'error'>(
    grant ? 'loading' : 'unavailable');
  const [stale, setStale] = useState(false);
  const [qr, setQr] = useState<string | null>(null);
  const hasSnapshot = useRef(false);
  const callbacks = useRef({ onReady, onAuthFailed, onPresentation });
  callbacks.current = { onReady, onAuthFailed, onPresentation };

  // The grant was refused somewhere. There is nothing the stage can do about it
  // — the assignment moved, the party ended, the television was unpaired, or
  // the grant simply reached its end — so it says so, and the shell decides.
  const reportAuthFailure = useCallback(() => {
    callbacks.current.onAuthFailed?.();
  }, []);

  // The same polling shape every other live Party surface uses: every
  // successful read is the current truth, a failure keeps what is on screen,
  // and a reconnect is just the next read. SINGLE-FLIGHT with a timeout (the
  // display's own poller): the next read is scheduled when the last one has
  // finished, so a slow answer never overlaps the next one and can never land
  // on top of a newer frame, and a read that never answers is given up on
  // instead of holding the stage. The poll keeps going after a 401 or a 404,
  // as it always has: a game that appears, or a grant that works again, is
  // simply the next read.
  usePoll({
    enabled: grant !== null,
    intervalMs: POLL_MS,
    timeoutMs: SNAPSHOT_TIMEOUT_MS,
    refreshKey,
    read: (signal) => getPartyDisplaySnapshot(grant!, signal),
    onValue: (next) => {
      if (!hasSnapshot.current) callbacks.current.onReady?.();
      hasSnapshot.current = true;
      setSnapshot(next);
      setConnection('ready');
      setStale(false);
    },
    onError: (error) => {
      const status = statusOf(error);
      // 401 means the grant is finished; 404 that there is no game to read.
      if (status === 401 || status === 404) {
        if (status === 401) reportAuthFailure();
        setConnection('unavailable');
        return;
      }
      // No answer, a timeout, a 5xx: keep the last frame, and say it is stale.
      if (hasSnapshot.current) setStale(true);
      else setConnection('error');
    },
  });

  // Is a live scene of the party on screen? An error card is not a scene.
  const presentationActive = connection === 'ready' && snapshot !== null;
  useEffect(() => {
    callbacks.current.onPresentation?.(presentationActive);
  }, [presentationActive]);

  // The join code, as pixels from the server. Fetched while the lobby is
  // showing it, kept for a return to the lobby (the code does not change for
  // the life of the party link), and retried if it failed for a reason that is
  // not an answer — until it arrives, the scene changes, or the stage goes.
  const wantsQr = stageScene(snapshot) === 'lobby';
  useEffect(() => {
    if (!grant || !wantsQr || qr !== null) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    const controller = new AbortController();
    const load = () => {
      getPartyDisplayJoinQr(grant, controller.signal)
        .then((svg) => { if (!cancelled) setQr(svg); })
        .catch((error: unknown) => {
          if (cancelled || isAbort(error)) return;
          const status = statusOf(error);
          if (status === 401) { reportAuthFailure(); return; }
          // A lobby without a code is still a lobby; a 4xx will not change.
          if (!isRetryableDisplayFailure(status)) return;
          timer = setTimeout(load, displayRetryDelayMs(attempt++));
        });
    };
    load();
    return () => {
      cancelled = true;
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [grant, wantsQr, qr, reportAuthFailure]);

  const mediaUrl = useDisplayMedia(snapshot?.challenge?.mediaUrl ?? null, grant, reportAuthFailure);

  return (
    <PartyTvStage
      snapshot={snapshot}
      connection={connection}
      stale={stale}
      lobbyQr={qr}
      mediaUrlOverride={mediaUrl}
    />
  );
}

/**
 * The activity photograph, fetched with the grant and exposed as an object URL.
 *
 * An `<img>` cannot send a header, so the bytes are fetched here and the DOM is
 * handed a `blob:` URL. The photograph belongs to ONE activity: a new activity
 * clears the previous picture at once — the card never shows the last round's
 * photograph over this round's words — and cancels any fetch or retry still
 * working for it. A failure that is not an answer is retried on a capped
 * backoff for as long as the activity is on screen. Every object URL is revoked:
 * when it is replaced, when the activity changes, and on unmount — an evening
 * of activities would otherwise leak a blob per round, on the device least able
 * to afford it.
 */
function useDisplayMedia(
  path: string | null, grant: string | null, onAuthFailure: () => void,
): string | null {
  const [url, setUrl] = useState<string | null>(null);

  const revoke = useCallback((value: string | null) => {
    if (value) URL.revokeObjectURL(value);
  }, []);

  useEffect(() => {
    if (!path || !grant) return;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    let attempt = 0;
    const controller = new AbortController();

    const load = () => {
      fetchPartyDisplayMedia(path, grant, controller.signal)
        .then((objectUrl) => {
          // A fetch that resolved after its activity left still made a URL.
          if (cancelled) { URL.revokeObjectURL(objectUrl); return; }
          setUrl((previous) => { revoke(previous); return objectUrl; });
        })
        .catch((error: unknown) => {
          if (cancelled || isAbort(error)) return;
          const status = statusOf(error);
          if (status === 401) { onAuthFailure(); return; }
          // The card renders without a photograph; a 404 will not change.
          if (!isRetryableDisplayFailure(status)) return;
          timer = setTimeout(load, displayRetryDelayMs(attempt++));
        });
    };
    load();

    return () => {
      cancelled = true;
      controller.abort();
      if (timer) clearTimeout(timer);
      // This activity's picture leaves with it.
      setUrl((previous) => { revoke(previous); return null; });
    };
  }, [path, grant, revoke, onAuthFailure]);

  // Last line of defence: whatever is held when the component goes away.
  const held = useRef<string | null>(null);
  held.current = url;
  useEffect(() => () => { revoke(held.current); }, [revoke]);

  return url;
}

/**
 * The grant, read once from the fragment.
 *
 * A fragment is never transmitted to a server, so it cannot appear in an access
 * log, a proxy trace or a Referer header — the same reason the recovery link
 * and the TV pairing secret use one. Nothing persists it: a reload legitimately
 * has no credential and must ask the shell for another.
 */
function readGrantFromFragment(): string | null {
  const raw = new URLSearchParams(window.location.hash.replace(/^#/, '')).get('grant');
  return raw && raw.length >= 16 ? raw : null;
}

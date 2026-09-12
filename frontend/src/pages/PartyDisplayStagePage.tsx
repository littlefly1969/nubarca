import { useCallback, useEffect, useRef, useState } from 'react';
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
  const [snapshot, setSnapshot] = useState<PartyGamePublicSnapshot | null>(null);
  const [connection, setConnection] = useState<'loading' | 'ready' | 'unavailable' | 'error'>(
    grant ? 'loading' : 'unavailable');
  const [stale, setStale] = useState(false);
  const [qr, setQr] = useState<string | null>(null);
  const hasSnapshot = useRef(false);

  // The fragment is removed from the address bar and from history before
  // anything else runs. It stays only in the closure above.
  useEffect(() => {
    if (window.location.hash) {
      window.history.replaceState(null, '', window.location.pathname + window.location.search);
    }
  }, []);

  // The renderer heartbeat. The shell treats silence as a dead page and
  // remounts, so this must keep running for as long as the document does.
  useEffect(() => {
    const beat = () => postToShell({ type: 'display-heartbeat', protocol: BRIDGE_PROTOCOL, at: Date.now() });
    beat();
    const timer = setInterval(beat, HEARTBEAT_MS);
    return () => clearInterval(timer);
  }, []);

  // The grant was refused somewhere. There is nothing this page can do about
  // it — the assignment moved, the party ended, the television was unpaired, or
  // the grant simply reached its end — so it says so, and the shell decides.
  const reportAuthFailure = useCallback(() => {
    postToShell({ type: 'display-auth-failed' });
  }, []);

  // The same polling shape every other live Party surface uses: every
  // successful read is the current truth, a failure keeps what is on screen,
  // and a reconnect is just the next read.
  useEffect(() => {
    if (!grant) return;
    let cancelled = false;
    const controller = new AbortController();

    const read = async () => {
      try {
        const next = await getPartyDisplaySnapshot(grant, controller.signal);
        if (cancelled) return;
        if (!hasSnapshot.current) postToShell({ type: 'display-ready' });
        hasSnapshot.current = true;
        setSnapshot(next);
        setConnection('ready');
        setStale(false);
      } catch (error) {
        if (cancelled || isAbort(error)) return;
        const status = statusOf(error);
        // 401 means the grant is finished; 404 that there is no game to read.
        // Either way this page shows no party, and a 401 is the shell's to act on.
        if (status === 401 || status === 404) {
          if (status === 401) reportAuthFailure();
          setConnection('unavailable');
          return;
        }
        if (hasSnapshot.current) setStale(true);
        else setConnection('error');
      }
    };

    void read();
    const timer = setInterval(() => void read(), POLL_MS);
    return () => { cancelled = true; controller.abort(); clearInterval(timer); };
  }, [grant, reportAuthFailure]);

  // Is a live scene of the party on screen? The shell needs exactly this for
  // its keep-awake policy, and nothing more. An error card is not a scene.
  const presentationActive = connection === 'ready' && snapshot !== null;
  useEffect(() => {
    postToShell({ type: 'display-presentation', active: presentationActive });
  }, [presentationActive]);

  // The join code, as pixels from the server. Fetched while the lobby is
  // showing it, kept for a return to the lobby (the code does not change for
  // the life of the party link), and retried if it failed for a reason that is
  // not an answer — until it arrives, the scene changes, or the page goes.
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

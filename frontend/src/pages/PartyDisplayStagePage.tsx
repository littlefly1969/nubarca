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
// The native shell owns the lifecycle: it mints the grant, it watches this page
// through the heartbeat below, and it remounts the WebView when the renderer
// dies. This page owns the presentation and nothing else.

const POLL_MS = 2_500;
// The renderer heartbeat the native shell watches. It measures THIS page being
// alive, not the party server: a wedged JS context still answers no beats.
const HEARTBEAT_MS = 2_000;

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
    const beat = () => {
      const bridge = (window as unknown as {
        ReactNativeWebView?: { postMessage(data: string): void };
      }).ReactNativeWebView;
      try {
        bridge?.postMessage(JSON.stringify({ type: 'display-heartbeat', at: Date.now() }));
      } catch {
        // A shell that cannot hear us is the shell's problem to notice; the
        // page must not fall over because it is running in a plain browser.
      }
    };
    beat();
    const timer = setInterval(beat, HEARTBEAT_MS);
    return () => clearInterval(timer);
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
        hasSnapshot.current = true;
        setSnapshot(next);
        setConnection('ready');
        setStale(false);
      } catch (error) {
        if (cancelled || (error instanceof DOMException && error.name === 'AbortError')) return;
        // 401 means the grant is finished — the assignment moved, the party
        // ended, or the television was unpaired. There is nothing this page can
        // do about any of them; the shell decides what happens next.
        if (error instanceof ApiError && (error.status === 401 || error.status === 404)) {
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
  }, [grant]);

  // The join code, as pixels from the server. Fetched only while the lobby is
  // showing it, and only once per grant.
  const wantsQr = stageScene(snapshot) === 'lobby';
  useEffect(() => {
    if (!grant || !wantsQr || qr !== null) return;
    let cancelled = false;
    const controller = new AbortController();
    getPartyDisplayJoinQr(grant, controller.signal)
      .then((svg) => { if (!cancelled) setQr(svg); })
      .catch(() => { /* a lobby without a code is still a lobby */ });
    return () => { cancelled = true; controller.abort(); };
  }, [grant, wantsQr, qr]);

  const mediaUrl = useDisplayMedia(snapshot?.challenge?.mediaUrl ?? null, grant);

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
 * handed a `blob:` URL. Each one is revoked when the activity changes and on
 * unmount — an evening of activities would otherwise leak a blob per round, on
 * the device least able to afford it.
 */
function useDisplayMedia(path: string | null, grant: string | null): string | null {
  const [url, setUrl] = useState<string | null>(null);

  const revoke = useCallback((value: string | null) => {
    if (value) URL.revokeObjectURL(value);
  }, []);

  useEffect(() => {
    if (!path || !grant) { setUrl((previous) => { revoke(previous); return null; }); return; }
    let cancelled = false;
    const controller = new AbortController();
    let created: string | null = null;

    fetchPartyDisplayMedia(path, grant, controller.signal)
      .then((objectUrl) => {
        created = objectUrl;
        if (cancelled) { URL.revokeObjectURL(objectUrl); return; }
        setUrl((previous) => { revoke(previous); return objectUrl; });
      })
      .catch(() => { /* the card renders without a photograph */ });

    return () => {
      cancelled = true;
      controller.abort();
      // A fetch that resolved after unmount still made a URL. It is revoked by
      // the branch above; this handles the one already on screen.
      if (created === null) setUrl((previous) => { revoke(previous); return null; });
    };
  }, [path, grant, revoke]);

  // Last line of defence: whatever is held when the component goes away.
  useEffect(() => () => { revoke(url); }, [url, revoke]);

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

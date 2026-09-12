import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { StyleSheet, View } from 'react-native';
import { WebView } from 'react-native-webview';
import type { WebViewNavigation } from 'react-native-webview';
import { useI18n } from '../i18n';
import { useHostActive } from '../lib/useHostActive';
import { useScreenAwake } from '../lib/useScreenAwake';
import { shouldKeepPartyDisplayAwake } from '../video/wakePolicy';
import { mintPartyDisplayGrant } from '../api/tv';
import { ApiError, getBaseUrl } from '../api/client';
import {
  initialWatchdogState,
  recovering,
  rendererVisible,
  showsNativeFallback,
  watchdogReducer,
  type DisplayRendererEvent,
  type DisplayWatchdogState,
} from '../lib/partyDisplayWatchdog';
import {
  classifyMintFailure,
  mintRetryDelayMs,
  parseRendererMessage,
  renewDelayMs,
  stageUrl,
  RENDERER_PROTOCOL,
} from '../lib/partyDisplayGrant';
import { PartyNativeSurface, PARTY_SURFACE_BACKGROUND } from '../components/PartyNativeSurface';
import { tvDebug } from '../debug';

// The party game, on the television it was assigned to — mounted only while
// the SERVER's presentation for that party is `game`.
//
// The native shell stays the authority for everything that is not the picture:
// pairing, the session, the assignment, the grant and its renewal, the
// lifecycle, the watchdog, the cover, the fallback and the keep-awake. The
// WebView is authority for the PRESENTATION and nothing else — it renders the
// canonical party stage, the same one a browser or a projector shows, so a room
// cannot be given two different accounts of what the game is doing.
//
// It carries NO session cookie, NO owner credential and NO party token. The
// only thing it is given is a display grant, in the fragment, which the shell
// minted from the device's own session and which grants exactly one read.
//
// The parent keys this component by the server's assignment key, so Party A →
// Party B unmounts it entirely: A's renderer, A's grant and A's frame are gone
// in the same commit that accepts B, and B starts from nothing under the cover.

interface Props {
  /** The party's name, for the native surface that covers the stage while it loads. */
  readonly albumName: string | null;
  /** The TELEVISION's session is gone (a mint answered 401). */
  readonly onSessionInvalid: () => void;
  /** Ask the control plane for an immediate re-read: the capability says the party moved. */
  readonly onRequestAssignment: () => void;
}

type GrantState =
  | { kind: 'minting' }
  | { kind: 'ready'; token: string }
  /** No grant for now, and why: the party is not showable, or the server did not answer. */
  | { kind: 'waiting'; failure: 'not-assigned' | 'transient' };

function reduceWatchdog(state: DisplayWatchdogState, event: DisplayRendererEvent): DisplayWatchdogState {
  return watchdogReducer(state, event, Date.now());
}

/** The latest value of a prop, for callbacks that must stay stable across renders. */
function useLatest<T>(value: T): { readonly current: T } {
  const ref = useRef(value);
  ref.current = value;
  return ref;
}

export function PartyDisplayScreen({ albumName, onSessionInvalid, onRequestAssignment }: Props) {
  const { t } = useI18n();
  const [grant, setGrant] = useState<GrantState>({ kind: 'minting' });
  // Bumped to ask for a new grant: a renewal, a refused grant, a retry. Every
  // mint runs through the one effect below, so two can never be in flight.
  const [mintRequest, setMintRequest] = useState(0);
  const [watchdog, dispatch] = useReducer(reduceWatchdog, initialWatchdogState);
  // The page's own coarse signal: is a live scene of the party on screen.
  const [presentationActive, setPresentationActive] = useState(false);
  const hostActive = useHostActive();
  const webRef = useRef<WebView>(null);
  const baseUrl = getBaseUrl();

  const onSessionInvalidRef = useLatest(onSessionInvalid);
  const onRequestAssignmentRef = useLatest(onRequestAssignment);
  // Consecutive mint failures, for the backoff.
  const mintFailuresRef = useRef(0);
  // True while a mint is in flight. A 401 reported by the OLD renderer during a
  // renewal is the renewal working, not a failure to act on.
  const mintingRef = useRef(false);
  // A retry of a failed mint is already scheduled. A refusal reported by the
  // page meanwhile is answered by that retry, not by a second one.
  const retryPendingRef = useRef(false);
  // A re-mint for a refused grant is already scheduled; later reports of the
  // same refusal are the same event.
  const authRetryRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const authFailuresRef = useRef(0);

  // --- the grant ----------------------------------------------------------
  //
  // Minted when the game takes the screen, replaced BEFORE it lapses (minting
  // revokes the previous grant server-side, so the replacement is also its
  // revocation), and minted again whenever the page reports a refusal. A
  // transient failure is retried with a capped backoff for as long as the
  // server keeps saying `game`; a 404 fails closed and asks the control plane;
  // a 401 is the session, and goes to pairing.
  useEffect(() => {
    let cancelled = false;
    let next: ReturnType<typeof setTimeout> | undefined;
    const controller = new AbortController();
    mintingRef.current = true;
    retryPendingRef.current = false;
    tvDebug('party', 'grant-mint');
    mintPartyDisplayGrant(controller.signal)
      .then((minted) => {
        if (cancelled) return;
        mintingRef.current = false;
        mintFailuresRef.current = 0;
        tvDebug('party', 'grant-mint-ok');
        setGrant({ kind: 'ready', token: minted.grant });
        setPresentationActive(false);
        // A new grant is a new renderer, from nothing, behind the cover.
        dispatch({ type: 'start' });
        next = setTimeout(() => setMintRequest((n) => n + 1), renewDelayMs(minted, Date.now()));
      })
      .catch((err: unknown) => {
        if (cancelled || controller.signal.aborted) return;
        mintingRef.current = false;
        const failure = classifyMintFailure(err instanceof ApiError ? err.status : null);
        tvDebug('party', 'grant-mint-failed', failure);
        if (failure === 'session-invalid') {
          onSessionInvalidRef.current();
          return;
        }
        if (failure === 'not-assigned') onRequestAssignmentRef.current();
        // A RENEWAL that did not get through leaves a working stage alone: the
        // grant it holds is still good until it lapses, and if it is not, the
        // page reports the refusal. Only a party that stopped being showable, or
        // a first mint, puts the cover up.
        setGrant((current) => (current.kind === 'ready' && failure === 'transient'
          ? current
          : { kind: 'waiting', failure }));
        retryPendingRef.current = true;
        next = setTimeout(
          () => setMintRequest((n) => n + 1),
          mintRetryDelayMs(failure, mintFailuresRef.current++));
      });
    return () => {
      cancelled = true;
      mintingRef.current = false;
      controller.abort();
      if (next) clearTimeout(next);
    };
  }, [mintRequest, onSessionInvalidRef, onRequestAssignmentRef]);

  useEffect(() => () => {
    if (authRetryRef.current) clearTimeout(authRetryRef.current);
  }, []);

  // The renderer is alive and says its capability was refused. That is NOT
  // silence — the watchdog would never see it — so it is its own path: tell the
  // control plane (the assignment may have moved), and mint again, backing off
  // if the refusals keep coming.
  const onAuthFailed = useCallback(() => {
    if (mintingRef.current || retryPendingRef.current || authRetryRef.current !== null) return;
    tvDebug('party', 'display-auth-failed');
    onRequestAssignmentRef.current();
    const delay = mintRetryDelayMs('transient', authFailuresRef.current++);
    authRetryRef.current = setTimeout(() => {
      authRetryRef.current = null;
      setMintRequest((n) => n + 1);
    }, delay);
  }, [onRequestAssignmentRef]);

  // --- the watchdog's clock ----------------------------------------------
  //
  // It has to be the SHELL's clock, because the thing it measures is a page
  // that may have stopped running its own. It does not run behind HOME, and on
  // return the renderer gets its windows again rather than being declared
  // wedged for a silence that was the platform's.
  useEffect(() => {
    if (!hostActive) return;
    dispatch({ type: 'resume' });
    const timer = setInterval(() => dispatch({ type: 'tick' }), 1_000);
    return () => clearInterval(timer);
  }, [hostActive]);

  // What the watchdog did, for the debug log. Transitions only.
  const previous = useRef(watchdog);
  useEffect(() => {
    const before = previous.current;
    previous.current = watchdog;
    if (watchdog.generation !== before.generation && watchdog.mounted) {
      tvDebug('party', before.gaveUp ? 'webview-probe' : 'webview-mounted', watchdog.generation);
    }
    if (watchdog.lastBeatAt !== null && before.lastBeatAt === null) tvDebug('party', 'first-heartbeat');
    if (watchdog.readyAt !== null && before.readyAt === null) tvDebug('party', 'first-snapshot-ready');
    if (!watchdog.mounted && before.mounted && watchdog.remountAt !== null) {
      tvDebug('party', watchdog.gaveUp ? 'recovery-slow' : 'recovery-fast', watchdog.fastRecoveries);
    }
    if (watchdog.gaveUp && !before.gaveUp) tvDebug('party', 'fallback-entered');
    if (!watchdog.gaveUp && before.gaveUp) tvDebug('party', 'recovery-success');
  }, [watchdog]);

  const onMessage = useCallback((raw: string) => {
    const message = parseRendererMessage(raw);
    if (message === null) return; // silence, which is already handled
    switch (message.kind) {
      case 'heartbeat':
        dispatch({ type: 'heartbeat' });
        // A page older than this shell sends beats and nothing else. Alive is
        // the only proof it can give, so for it alive IS ready.
        if (message.protocol < RENDERER_PROTOCOL) {
          dispatch({ type: 'ready' });
          setPresentationActive(true);
        }
        return;
      case 'ready':
        dispatch({ type: 'ready' });
        authFailuresRef.current = 0;
        return;
      case 'presentation':
        setPresentationActive(message.active);
        return;
      case 'auth-failed':
        onAuthFailed();
        return;
    }
  }, [onAuthFailed]);

  const visible = grant.kind === 'ready' && rendererVisible(watchdog);

  // A game on screen IS active playback, through the SAME lock the slideshow
  // uses rather than a second authority — the policy lives in wakePolicy.ts with
  // every other answer to "hold the screen". The page decides whether a live
  // scene is up; the shell decides whether that page is on screen at all.
  useScreenAwake(shouldKeepPartyDisplayAwake({ hostActive, showing: visible, presentationActive }));

  const stagePrefix = `${baseUrl}/party-display/`;
  const coverMessage = grant.kind === 'waiting'
    ? t(grant.failure === 'not-assigned' ? 'partyDisplay.unavailable' : 'partyDisplay.reconnecting')
    : showsNativeFallback(watchdog) || recovering(watchdog)
      ? t('partyDisplay.reconnecting')
      : t('partyDisplay.starting');

  return (
    <View style={styles.fill}>
      {grant.kind === 'ready' && watchdog.mounted && (
        <WebView
          // A dead renderer is REPLACED, never revived: a new generation is a
          // new WebView, and a new grant is a new generation.
          key={`stage-${watchdog.generation}`}
          ref={webRef}
          source={{ uri: stageUrl(baseUrl, grant.token) }}
          style={styles.stage}
          // --- display-only hardening -----------------------------------
          // Nothing in the page may take focus, scroll, or be typed into: this
          // is a screen in a corner, and the D-pad belongs to the shell.
          focusable={false}
          scrollEnabled={false}
          overScrollMode="never"
          scalesPageToFit={false}
          // Fail closed on navigation. The stage is one document on the
          // configured origin; anything else — a link, a redirect, a file://,
          // a popup — is refused rather than followed.
          originWhitelist={[getBaseUrl()]}
          setSupportMultipleWindows={false}
          javaScriptCanOpenWindowsAutomatically={false}
          allowFileAccess={false}
          allowFileAccessFromFileURLs={false}
          allowUniversalAccessFromFileURLs={false}
          mixedContentMode="never"
          // The TV session cookie lives in the native client and is never
          // shared with this WebView: it has no business on /api/tv, and the
          // grant is the only credential it is meant to hold.
          sharedCookiesEnabled={false}
          thirdPartyCookiesEnabled={false}
          incognito
          androidLayerType="hardware"
          onShouldStartLoadWithRequest={(request) => request.url.startsWith(stagePrefix)}
          onNavigationStateChange={(nav: WebViewNavigation) => {
            // Belt and braces: if anything did navigate away, replace the
            // renderer rather than showing whatever it reached.
            if (!nav.url.startsWith(stagePrefix)) {
              webRef.current?.stopLoading();
              dispatch({ type: 'renderer-gone' });
            }
          }}
          onMessage={(event) => onMessage(event.nativeEvent.data)}
          onRenderProcessGone={() => dispatch({ type: 'renderer-gone' })}
          onContentProcessDidTerminate={() => dispatch({ type: 'renderer-gone' })}
          // The DOCUMENT never arrived: no network, the frontend restarting, a
          // proxy's 502. A renderer that loaded an error page is as dead as one
          // that crashed, and it must not be what the room sees.
          onError={() => dispatch({ type: 'load-error' })}
          onHttpError={() => dispatch({ type: 'load-error' })}
        />
      )}

      {/* The shell outlives its renderer and says something true, rather than
          leaving a black rectangle in somebody's living room. It stays over
          the stage until the page has proved it is alive AND drawn a real
          snapshot, so a takeover, a remount and a renewal all look like the
          same calm card — and a probe mounted behind the fallback is never
          seen until it has proved itself. */}
      {!visible && (
        <PartyNativeSurface
          overlay
          testID="party-display-native-fallback"
          albumName={albumName}
          message={coverMessage}
          busy={grant.kind !== 'waiting' || grant.failure === 'transient'}
        />
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: PARTY_SURFACE_BACKGROUND },
  // The WebView's own ground, so the frames before the page paints are the
  // stage's colour and never Android's white.
  stage: { flex: 1, backgroundColor: PARTY_SURFACE_BACKGROUND },
});

import { useCallback, useEffect, useReducer, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { WebView } from 'react-native-webview';
import type { WebViewNavigation } from 'react-native-webview';
import { colors, font, spacing } from '../theme';
import { useI18n } from '../i18n';
import { useHostActive } from '../lib/useHostActive';
import { useScreenAwake } from '../lib/useScreenAwake';
import { shouldKeepPartyDisplayAwake } from '../video/wakePolicy';
import { mintPartyDisplayGrant } from '../api/tv';
import { getBaseUrl } from '../api/client';
import {
  initialWatchdogState,
  showsNativeFallback,
  watchdogReducer,
  type DisplayRendererEvent,
} from '../lib/partyDisplayWatchdog';

// The party game, on the television it was assigned to.
//
// The native shell stays the authority for everything that is not the picture:
// pairing, the session, the assignment, the grant, the lifecycle, the watchdog,
// the fallback and the keep-awake. The WebView is authority for the
// PRESENTATION and nothing else — it renders the canonical party stage, the
// same one a browser or a projector shows, so a room cannot be given two
// different accounts of what the game is doing.
//
// It carries NO session cookie, NO owner credential and NO party token. The
// only thing it is given is a display grant, in the fragment, which the shell
// minted from the device's own session and which grants exactly one read.

interface Props {
  /** Bumped by the caller to force a fresh grant + mount, e.g. on Party A → B. */
  readonly assignmentKey: string;
}

type Bootstrap =
  | { kind: 'minting' }
  | { kind: 'ready'; url: string }
  /** Paired and assigned, but the party cannot be shown right now. */
  | { kind: 'unavailable' };

export function PartyDisplayScreen({ assignmentKey }: Props) {
  const { t } = useI18n();
  const [bootstrap, setBootstrap] = useState<Bootstrap>({ kind: 'minting' });
  const [watchdog, dispatch] = useReducer(
    (state: typeof initialWatchdogState, event: DisplayRendererEvent) =>
      watchdogReducer(state, event, Date.now()),
    initialWatchdogState);
  const hostActive = useHostActive();
  const webRef = useRef<WebView>(null);

  // A party on screen IS active playback. The television must not fall asleep
  // during a game any more than it does during a slideshow, and this reuses the
  // SAME lock the slideshow uses rather than adding a second authority — the
  // policy lives in wakePolicy.ts with every other answer to "hold the screen".
  useScreenAwake(shouldKeepPartyDisplayAwake({
    hostActive,
    showing: bootstrap.kind === 'ready' && !showsNativeFallback(watchdog),
  }));

  // A grant per assignment, and a fresh one whenever the assignment changes.
  // Minting revokes the previous grant server-side, so the television can never
  // hold two.
  useEffect(() => {
    let cancelled = false;
    setBootstrap({ kind: 'minting' });
    mintPartyDisplayGrant()
      .then((grant) => {
        if (cancelled) return;
        // The grant travels in the FRAGMENT: browsers never send it to a
        // server, so it cannot reach an access log or a Referer header. The
        // page strips it from history immediately and keeps it in memory only.
        setBootstrap({
          kind: 'ready',
          url: `${getBaseUrl()}/party-display/stage#grant=${encodeURIComponent(grant.grant)}`,
        });
      })
      .catch(() => { if (!cancelled) setBootstrap({ kind: 'unavailable' }); });
    return () => { cancelled = true; };
  }, [assignmentKey]);

  // The watchdog's own clock. It has to be the SHELL's, because the thing it is
  // measuring is a page that may have stopped running its own.
  useEffect(() => {
    const timer = setInterval(() => dispatch({ type: 'tick' }), 1_000);
    return () => clearInterval(timer);
  }, []);

  const onMessage = useCallback((raw: string) => {
    try {
      const message = JSON.parse(raw) as { type?: string };
      if (message.type === 'display-heartbeat') dispatch({ type: 'heartbeat' });
    } catch {
      // A malformed beat is silence, and silence is already handled.
    }
  }, []);

  const fallback = showsNativeFallback(watchdog);

  return (
    <View style={styles.fill}>
      {bootstrap.kind === 'ready' && !fallback && (
        <WebView
          // A dead renderer is REPLACED, never revived: a new generation is a
          // new WebView.
          key={`${assignmentKey}:${watchdog.generation}`}
          ref={webRef}
          source={{ uri: bootstrap.url }}
          style={styles.fill}
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
          onShouldStartLoadWithRequest={(request) =>
            request.url.startsWith(`${getBaseUrl()}/party-display/`)}
          onNavigationStateChange={(nav: WebViewNavigation) => {
            // Belt and braces: if anything did navigate away, go back to the
            // stage rather than showing whatever it reached.
            if (!nav.url.startsWith(`${getBaseUrl()}/party-display/`)) {
              webRef.current?.stopLoading();
              dispatch({ type: 'renderer-gone' });
            }
          }}
          onMessage={(event) => onMessage(event.nativeEvent.data)}
          onRenderProcessGone={() => dispatch({ type: 'renderer-gone' })}
          onContentProcessDidTerminate={() => dispatch({ type: 'renderer-gone' })}
        />
      )}

      {/* The shell outlives its renderer and says something true, rather than
          leaving a black rectangle in somebody's living room. It keeps
          retrying slowly behind this, so nobody has to walk to the TV. */}
      {(fallback || bootstrap.kind !== 'ready') && (
        <View style={styles.notice} testID="party-display-native-fallback">
          <Text style={styles.brand}>{t('partyDisplay.brand')}</Text>
          <Text style={styles.message}>
            {bootstrap.kind === 'unavailable'
              ? t('partyDisplay.unavailable')
              : t('partyDisplay.reconnecting')}
          </Text>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  fill: { flex: 1, backgroundColor: '#070a12' },
  notice: {
    position: 'absolute', top: 0, right: 0, bottom: 0, left: 0,
    alignItems: 'center', justifyContent: 'center',
    backgroundColor: '#070a12', padding: spacing.xl, gap: spacing.md,
  },
  brand: {
    color: '#9db8ff', fontSize: font.body, fontWeight: '800',
    letterSpacing: 4, textTransform: 'uppercase',
  },
  message: {
    color: colors.text, fontSize: font.heading, textAlign: 'center', maxWidth: 900,
  },
});

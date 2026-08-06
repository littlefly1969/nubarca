import React, { useCallback, useEffect, useReducer, useState } from 'react';
import { ActivityIndicator, BackHandler, StyleSheet, Text, View } from 'react-native';
import { StatusBar } from 'expo-status-bar';
import Constants from 'expo-constants';
import { colors, font } from './src/theme';
import { ApiError, clearSession, configure, restoreSession } from './src/api/client';
import {
  clearPersonalGrant,
  getTvPersonalHome,
  getTvPersonalStatus,
  lockTvPersonal,
} from './src/api/personal';
import { getTvSession, type TvAlbum, type TvAlbumItem } from './src/api/tv';
import {
  flowEffects,
  initialFlowState,
  tvFlowReducer,
  type TvFlowState,
} from './src/personal/flow';
import { PairingScreen } from './src/screens/PairingScreen';
import { ModeSelectScreen } from './src/screens/ModeSelectScreen';
import { PinEntryScreen } from './src/screens/PinEntryScreen';
import { PersonalHomeScreen } from './src/screens/PersonalHomeScreen';
import { PersonalGalleryScreen } from './src/screens/PersonalGalleryScreen';
import { PersonalVideosScreen } from './src/screens/PersonalVideosScreen';
import { BeautyLabScreen } from './src/screens/BeautyLabScreen';
import { AlbumsScreen } from './src/screens/AlbumsScreen';
import { AlbumItemsScreen } from './src/screens/AlbumItemsScreen';
import { ViewerScreen } from './src/screens/ViewerScreen';
import { I18nProvider, useI18n, toLanguage } from './src/i18n';
import { startBackgroundUpdateCheck } from './src/ota/expoUpdate';

// Top-level navigation is the explicit mode state machine in
// src/personal/flow.ts (pairing → mode selection → Party | Personal Area).
// Party keeps its own shallow sub-navigation below (albums → items → viewer),
// unchanged from before; it is reset every time Party is entered.
type PartyScreen =
  | { name: 'albums' }
  | { name: 'items'; album: TvAlbum }
  | {
      name: 'viewer';
      // The album the viewer was opened from, so Back returns to its grid (not
      // all the way to the album list) — matching the browser /tv behavior.
      album: TvAlbum;
      items: TvAlbumItem[];
      index: number;
      autoPlay: boolean;
      albumId: string;
      partyEnabled: boolean;
      partyUrl: string | null;
      partyUploadUrl: string | null;
    };

function resolveBaseUrl(): string {
  // Prefer the runtime EXPO_PUBLIC_* env var (inlined by Expo at build time), so
  // a Fire Stick test build can be pointed at production without editing source;
  // fall back to the app.config.js `extra.apiBaseUrl` (env-derived at build time),
  // then a localhost default. No secrets — just a base URL.
  const envUrl = process.env.EXPO_PUBLIC_NUBARCA_API_BASE_URL;
  const extra = (Constants.expoConfig?.extra ?? {}) as { apiBaseUrl?: string };
  return (envUrl || extra.apiBaseUrl || 'http://localhost:5177').replace(/\/$/, '');
}

// Root: provides i18n to the whole TV app. The active language is the paired
// owner's UI language (adopted from the TV session once resolved); Italian is
// the default until then.
export default function App(): React.JSX.Element {
  return (
    <I18nProvider>
      <AppInner />
    </I18nProvider>
  );
}

function AppInner(): React.JSX.Element {
  const { t, setLanguage } = useI18n();
  const [flow, rawDispatch] = useReducer(tvFlowReducer, initialFlowState);
  const [partyScreen, setPartyScreen] = useState<PartyScreen>({ name: 'albums' });
  // Current flow state as a ref, so stable callbacks can compute the required
  // teardown effects (flowEffects) for the state they fire from.
  const flowRef = React.useRef<TvFlowState>(flow);
  flowRef.current = flow;

  useEffect(() => {
    // Fire-and-forget: startup never waits for the OTA server. A downloaded
    // update is deliberately left pending until a later cold launch.
    startBackgroundUpdateCheck();
    configure(resolveBaseUrl());
    let cancelled = false;
    // On launch: rehydrate any persisted limited TV session cookie, then VALIDATE
    // it against GET /api/tv/session before trusting it. A live session lands on
    // MODE SELECTION (never a remembered mode, never unlocked — the personal
    // grant only ever lives in memory); a revoked/expired/absent session clears
    // the stored cookie and shows pairing.
    void restoreSession()
      .then(() => getTvSession())
      .then((session) => {
        if (cancelled) return;
        const lang = toLanguage(session.language);
        if (lang) setLanguage(lang);
        rawDispatch({ type: 'SESSION_READY' });
      })
      .catch(() => {
        if (cancelled) return;
        clearPersonalGrant();
        clearSession();
        rawDispatch({ type: 'SESSION_INVALID' });
      });
    return () => {
      cancelled = true;
    };
  }, [setLanguage]);

  const onPaired = useCallback(() => {
    // Just paired: the session cookie is set — adopt the owner's UI language
    // before showing mode selection (best-effort; defaults to Italian).
    getTvSession()
      .then((session) => {
        const lang = toLanguage(session.language);
        if (lang) setLanguage(lang);
      })
      .catch(() => { /* keep the current/default language */ });
    rawDispatch({ type: 'SESSION_READY' });
  }, [setLanguage]);

  // The limited TV session was revoked by the owner (or expired): tear down
  // EVERYTHING — in-memory personal grant, persisted cookie, cached derived
  // media — and return to pairing. Valid from every state (mode selector, PIN
  // entry, Personal Area, Party).
  const onSessionInvalid = useCallback(() => {
    const effects = flowEffects(flowRef.current, { type: 'SESSION_INVALID' });
    if (effects.dropGrant) clearPersonalGrant();
    if (effects.clearSession) clearSession();
    rawDispatch({ type: 'SESSION_INVALID' });
  }, []);

  // Paired session whose owner has NO Personal Area PIN: legacy/corrupted
  // state the atomic pairing flow can no longer produce. Tear down (grant,
  // persisted cookie, cached media) and show the "pairing is incomplete"
  // recovery — re-pairing forcibly creates the PIN.
  const onAssociationIncomplete = useCallback(() => {
    const effects = flowEffects(flowRef.current, { type: 'ASSOCIATION_INCOMPLETE' });
    if (effects.dropGrant) clearPersonalGrant();
    if (effects.clearSession) clearSession();
    rawDispatch({ type: 'ASSOCIATION_INCOMPLETE' });
  }, []);

  // Leaving the Personal Area root: lock IMMEDIATELY. lockTvPersonal drops the
  // in-memory grant synchronously and then best-effort revokes it server-side
  // (the bounded server lifetime covers a lost call). Idempotent. `reason`
  // 'pinChanged' (server-detected stale PIN generation) surfaces the "PIN was
  // changed" notice on the mode selector — never a trip back to pairing.
  const onLock = useCallback((reason?: 'pinChanged') => {
    void lockTvPersonal();
    rawDispatch({ type: 'LOCK', reason });
  }, []);

  // The mode selector performs no API calls by itself, so a revoked session —
  // or an invalid PIN-less association — would otherwise sit there looking
  // usable. Validate on entry and every 60s: 401 → pairing (full teardown);
  // pinConfigured=false → incomplete-association recovery.
  useEffect(() => {
    if (flow.name !== 'mode') return;
    let cancelled = false;
    const check = () => {
      getTvPersonalStatus()
        .then((status) => {
          if (!cancelled && !status.pinConfigured) onAssociationIncomplete();
        })
        .catch((err: unknown) => {
          // Only a definitive 401 unpairs; a transient network error must not.
          if (!cancelled && err instanceof ApiError && err.status === 401) {
            onSessionInvalid();
          }
        });
    };
    check();
    const timer = setInterval(check, 60_000);
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [flow.name, onSessionInvalid, onAssociationIncomplete]);

  // While inside the Personal Area, re-validate the grant every 15s so a PIN
  // change (or server-side revocation) evicts this TV promptly — not merely on
  // the next user action. 403 pin_changed → mode selector with the notice;
  // any other 403 → plain lock; 401 → pairing.
  // The ONE 15s grant re-validation loop spans the Personal Area AND the Beauty
  // Lab (both live behind the same in-memory grant); no extra permanent timer.
  const inPersonalArea =
    flow.name === 'personalHome' || flow.name === 'personalGallery'
    || flow.name === 'personalVideos' || flow.name === 'beautyLab';
  useEffect(() => {
    if (!inPersonalArea) return;
    const timer = setInterval(() => {
      getTvPersonalHome().catch((err: unknown) => {
        if (err instanceof ApiError && err.status === 401) {
          onSessionInvalid();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          const body = err.body as { error?: string } | null;
          onLock(body?.error === 'pin_changed' ? 'pinChanged' : undefined);
        }
        // Transient errors: keep the current screen; the next tick retries.
      });
    }, 15_000);
    return () => clearInterval(timer);
  }, [inPersonalArea, onLock, onSessionInvalid]);

  // Entering Party always starts at the album list (personal/mode state never
  // leaks into Party, and Party never resumes mid-viewer from a previous run).
  const onChooseParty = useCallback(() => {
    setPartyScreen({ name: 'albums' });
    rawDispatch({ type: 'CHOOSE_PARTY' });
  }, []);

  // TV remote Back button inside Party: AlbumItemsScreen and ViewerScreen own
  // their OWN BackHandler (each hides its MENU overlay first, then navigates up
  // via onClose/onBack). On the album list (Party root) the handler below
  // returns to MODE SELECTION (no PIN needed to come back to Party). On the
  // mode selector is the navigation root: one final BACK explicitly finishes
  // the Android activity (the platform default was not reliable on Fire TV).
  useEffect(() => {
    if (flow.name !== 'party' || partyScreen.name !== 'albums') return;
    const onBackPress = () => {
      rawDispatch({ type: 'PARTY_EXIT' });
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [flow.name, partyScreen.name]);

  useEffect(() => {
    if (flow.name !== 'mode' && flow.name !== 'pairing') return;
    const onBackPress = () => {
      BackHandler.exitApp();
      return true;
    };
    const sub = BackHandler.addEventListener('hardwareBackPress', onBackPress);
    return () => sub.remove();
  }, [flow.name]);

  return (
    <>
      <StatusBar style="light" />
      {flow.name === 'loading' && (
        <View style={styles.center}>
          <ActivityIndicator size="large" color={colors.accent} />
          <Text style={styles.text}>{t('app.connecting')}</Text>
        </View>
      )}
      {flow.name === 'pairing' && (
        <PairingScreen
          onPaired={onPaired}
          notice={flow.incomplete ? t('pairing.incomplete') : null}
        />
      )}
      {flow.name === 'mode' && (
        <ModeSelectScreen
          onChooseParty={onChooseParty}
          onChoosePersonal={() => rawDispatch({ type: 'CHOOSE_PERSONAL' })}
          onChooseBeautyLab={() => rawDispatch({ type: 'CHOOSE_BEAUTY_LAB' })}
          notice={flow.notice === 'pinChanged' ? t('mode.pinChangedNotice') : null}
        />
      )}
      {flow.name === 'pin' && (
        <PinEntryScreen
          onCancel={() => rawDispatch({ type: 'PIN_CANCELLED' })}
          onUnlocked={(home) => rawDispatch({ type: 'UNLOCKED', home })}
          onSessionInvalid={onSessionInvalid}
          onAssociationIncomplete={onAssociationIncomplete}
        />
      )}
      {flow.name === 'personalHome' && (
        <PersonalHomeScreen
          home={flow.home}
          onOpenGallery={() => rawDispatch({ type: 'OPEN_GALLERY' })}
          onOpenVideos={() => rawDispatch({ type: 'OPEN_VIDEOS' })}
          onLock={onLock}
        />
      )}
      {flow.name === 'personalGallery' && (
        <PersonalGalleryScreen
          onBack={() => rawDispatch({ type: 'GALLERY_BACK' })}
          onGrantInvalid={onLock}
          onSessionInvalid={onSessionInvalid}
        />
      )}
      {flow.name === 'personalVideos' && (
        <PersonalVideosScreen
          onBack={() => rawDispatch({ type: 'VIDEOS_BACK' })}
          onGrantInvalid={onLock}
          onSessionInvalid={onSessionInvalid}
        />
      )}
      {flow.name === 'beautyLab' && (
        <BeautyLabScreen
          // BACK from the Beauty Lab root LOCKS and returns to mode selection —
          // exactly the Personal Area security behaviour (onLock revokes the grant).
          onLock={onLock}
          onSessionInvalid={onSessionInvalid}
        />
      )}
      {flow.name === 'party' && partyScreen.name === 'albums' && (
        <AlbumsScreen
          onOpenAlbum={(album) => setPartyScreen({ name: 'items', album })}
          onSessionInvalid={onSessionInvalid}
        />
      )}
      {flow.name === 'party' && partyScreen.name === 'items' && (
        <AlbumItemsScreen
          album={partyScreen.album}
          onBack={() => setPartyScreen({ name: 'albums' })}
          onOpenItem={(items, index, autoPlay, ctx) =>
            setPartyScreen({
              name: 'viewer', album: partyScreen.album, items, index, autoPlay,
              albumId: ctx.albumId,
              partyEnabled: ctx.partyEnabled,
              partyUrl: ctx.partyUrl,
              partyUploadUrl: ctx.partyUploadUrl,
            })}
          onSessionInvalid={onSessionInvalid}
        />
      )}
      {flow.name === 'party' && partyScreen.name === 'viewer' && (
        <ViewerScreen
          items={partyScreen.items}
          startIndex={partyScreen.index}
          autoPlay={partyScreen.autoPlay}
          albumId={partyScreen.albumId}
          albumName={partyScreen.album.name}
          partyEnabled={partyScreen.partyEnabled}
          partyUrl={partyScreen.partyUrl}
          partyUploadUrl={partyScreen.partyUploadUrl}
          onClose={() => setPartyScreen({ name: 'items', album: partyScreen.album })}
          onSessionInvalid={onSessionInvalid}
        />
      )}
    </>
  );
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: colors.bg },
  text: { color: colors.muted, fontSize: font.body, marginTop: 16 },
});

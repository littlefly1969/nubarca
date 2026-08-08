import { useCallback, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import {
  PERMISSIONS,
  createCastGrant,
  revokeCastGrant,
  type CastGrant,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { currentUser, hasPermission } from '../auth/permissions';
import { nextPreparationDelayMs } from '../video/preparationPolling';
import {
  browserSupportsCastSender,
  isReceiverReachableOrigin,
  isSecureCastOrigin,
  loadGoogleCastSdk,
  type CastSdk,
} from './googleCastSdk';
import type { RemotePlayerControllerLike, RemotePlayerLike } from './castSdkTypes';
import {
  CastContext,
  type CastAvailability,
  type CastContextValue,
  type CastError,
  type CastHandoff,
  type CastPlaybackStatus,
  type CastRemoteState,
  type CastRequest,
  type CastSessionState,
} from './CastContext';

// The single owner of everything Cast: the SDK handle, the session, the
// RemotePlayer mirror, and the LIFETIME OF THE GRANT.
//
// The grant is the part that matters most. Exactly one is live at a time, it is
// held in a ref — never in state, never in web storage, never in a URL the
// browser records in history — and every path that ends or replaces a cast
// revokes it. The server's expiry is the backstop for the paths that cannot run
// (a closed laptop, a killed tab); it is a backstop, not the plan.
//
// Remote control is the framework's RemotePlayer/RemotePlayerController, not a
// polling loop of ours. That is what makes a pause from the TV remote, from the
// Google Home app or from another phone appear here: to the receiver they are
// the same state change, and the framework reports it once.

/** Give up waiting for an HLS ladder after this long, and say so. */
const PREPARATION_TIMEOUT_MS = 120_000;

const EMPTY_REMOTE: CastRemoteState = {
  deviceName: null,
  title: null,
  fileId: null,
  mode: null,
  isPaused: true,
  isMuted: false,
  currentTime: 0,
  duration: 0,
  volumeLevel: 1,
};

export function CastProvider({ children }: { children: ReactNode }) {
  const { state } = useAuth();
  const mayCast = hasPermission(currentUser(state), PERMISSIONS.castAccess);

  const [availability, setAvailability] = useState<CastAvailability>('unknown');
  const [sessionState, setSessionState] = useState<CastSessionState>('none');
  const [status, setStatus] = useState<CastPlaybackStatus>('idle');
  const [error, setError] = useState<CastError>(null);
  const [remote, setRemote] = useState<CastRemoteState | null>(null);

  const sdkRef = useRef<CastSdk | null>(null);
  const playerRef = useRef<RemotePlayerLike | null>(null);
  const controllerRef = useRef<RemotePlayerControllerLike | null>(null);
  // The live capability. A ref, so revoking never waits on a render, and so the
  // secret is not part of any state snapshot a devtool would print.
  const grantRef = useRef<CastGrant | null>(null);
  // What we asked the receiver to play, for the mini controller's label.
  const itemRef = useRef<{ fileId: string; title: string } | null>(null);
  // The last position the receiver reported. Read when a session ends, so local
  // playback resumes where the television actually got to rather than at zero.
  const lastPositionRef = useRef(0);
  const handoffRef = useRef<CastHandoff | null>(null);
  const mountedRef = useRef(true);

  useEffect(() => () => { mountedRef.current = false; }, []);

  // ── receiver mirror ─────────────────────────────────────────────────────

  const readDeviceName = useCallback((): string | null => {
    const context = sdkRef.current?.framework.CastContext.getInstance();
    const session = context?.getCurrentSession() ?? null;
    return session?.getCastDevice?.()?.friendlyName ?? null;
  }, []);

  const syncRemote = useCallback(() => {
    const player = playerRef.current;
    if (player === null || !player.isConnected) {
      setRemote(null);
      return;
    }
    if (Number.isFinite(player.currentTime) && player.currentTime > 0) {
      lastPositionRef.current = player.currentTime;
    }
    setRemote({
      ...EMPTY_REMOTE,
      deviceName: readDeviceName(),
      title: itemRef.current?.title ?? null,
      fileId: itemRef.current?.fileId ?? null,
      mode: grantRef.current?.mode ?? null,
      isPaused: player.isPaused,
      isMuted: player.isMuted,
      currentTime: Number.isFinite(player.currentTime) ? player.currentTime : 0,
      duration: Number.isFinite(player.duration) ? player.duration : 0,
      volumeLevel: Number.isFinite(player.volumeLevel) ? player.volumeLevel : 1,
    });
  }, [readDeviceName]);

  // Everything a finished cast has to clean up, in one place, so an explicit
  // stop and an unexpected disconnect cannot drift apart.
  const teardown = useCallback(() => {
    const grant = grantRef.current;
    grantRef.current = null;
    if (grant !== null) {
      void revokeCastGrant(grant.grantId);
    }

    const item = itemRef.current;
    if (item !== null) {
      // Preserve where the television actually reached. Never reset to zero:
      // losing the position is what makes a dropped connection feel like data
      // loss rather than an interruption.
      handoffRef.current = { fileId: item.fileId, positionSeconds: lastPositionRef.current };
    }
    itemRef.current = null;

    if (!mountedRef.current) return;
    setRemote(null);
    setSessionState('none');
    setStatus('idle');
    setError(null);
  }, []);

  // ── framework wiring ────────────────────────────────────────────────────

  const initialiseFramework = useCallback((sdk: CastSdk) => {
    const { framework, chrome } = sdk;
    const context = framework.CastContext.getInstance();

    context.setOptions({
      // Phase 1 uses Google's Default Media Receiver. NubArca's authorization
      // happens BEFORE playback, inside the grant, so the receiver needs no
      // login, no custom UI, no messaging and no analytics of its own — which
      // is exactly the threshold at which registering a custom receiver would
      // start to be worth it, and it is not met.
      receiverApplicationId: chrome.cast.media.DEFAULT_MEDIA_RECEIVER_APP_ID,
      // ORIGIN_SCOPED: another NubArca tab on this origin rejoins the session;
      // a page on any other origin does not.
      autoJoinPolicy: chrome.cast.AutoJoinPolicy.ORIGIN_SCOPED,
    });

    const player = new framework.RemotePlayer();
    const controller = new framework.RemotePlayerController(player);
    playerRef.current = player;
    controllerRef.current = controller;

    // ONE listener for every field. The framework raises a change for pause,
    // seek, volume, mute and connection alike, whether it originated in this
    // browser, on the TV remote, in another sender or in the Google Home app —
    // so there is nothing to poll and no protocol of ours to keep in step.
    controller.addEventListener(framework.RemotePlayerEventType.ANY_CHANGE, () => {
      if (!mountedRef.current) return;
      if (playerRef.current?.isConnected !== true) {
        // The receiver went away: session ended, network dropped, or somebody
        // else took the television.
        if (grantRef.current !== null || itemRef.current !== null) {
          teardown();
        }
        return;
      }
      syncRemote();
    });

    context.addEventListener(framework.CastContextEventType.SESSION_STATE_CHANGED, (event) => {
      if (!mountedRef.current) return;
      const next = String((event as { sessionState?: string }).sessionState ?? '');
      const s = framework.SessionState;
      if (next === s.SESSION_STARTING) {
        setSessionState('connecting');
      } else if (next === s.SESSION_STARTED || next === s.SESSION_RESUMED) {
        setSessionState('connected');
        syncRemote();
      } else if (next === s.SESSION_ENDED || next === s.SESSION_START_FAILED) {
        teardown();
      }
    });

    // Reflect a session that already existed when this tab loaded.
    if (context.getCurrentSession() !== null) {
      setSessionState('connected');
      syncRemote();
    }
  }, [syncRemote, teardown]);

  // ── availability ────────────────────────────────────────────────────────
  //
  // Ordered so that the explanation the user reads names the ACTUAL obstacle.
  // A Firefox user is told their browser cannot do this; a user on plain http
  // is told to use HTTPS; a user on localhost is told the television cannot
  // resolve that address. "Cast is unavailable" would be true and useless.

  useEffect(() => {
    if (!mayCast) {
      setAvailability('no-permission');
      return;
    }
    if (!browserSupportsCastSender()) {
      setAvailability('unsupported');
      return;
    }
    if (!isSecureCastOrigin()) {
      setAvailability('insecure-origin');
      return;
    }
    if (!isReceiverReachableOrigin()) {
      setAvailability('unreachable-origin');
      return;
    }

    let cancelled = false;
    void loadGoogleCastSdk().then((load) => {
      if (cancelled || !mountedRef.current) return;
      if (load.status !== 'ready') {
        setAvailability(load.status === 'unsupported' ? 'unsupported' : 'failed');
        return;
      }
      sdkRef.current = load.sdk;
      initialiseFramework(load.sdk);
      setAvailability('available');
    });
    return () => { cancelled = true; };
    // Deliberately keyed on the PERMISSION alone. `initialiseFramework` closes
    // over stable callbacks; re-running this effect would register a second set
    // of framework listeners and double every state change.
  }, [mayCast, initialiseFramework]);

  // ── casting one video ───────────────────────────────────────────────────

  // Poll the grant endpoint while the installation prepares an HLS ladder. The
  // server enqueues the work on the first request, so this is passive waiting
  // on a bounded ramp — and it gives up rather than spinning forever.
  const acquireGrant = useCallback(async (fileId: string): Promise<CastGrant> => {
    const deadline = Date.now() + PREPARATION_TIMEOUT_MS;
    let attempt = 0;
    for (;;) {
      const result = await createCastGrant(fileId);
      if (result.status === 'ready') return result.grant;

      if (Date.now() >= deadline) throw new Error('preparing');
      if (mountedRef.current) setStatus('preparing');

      const delay = nextPreparationDelayMs(
        attempt,
        result.retryAfterSeconds === null ? null : String(result.retryAfterSeconds),
      );
      attempt += 1;
      await new Promise((resolve) => { setTimeout(resolve, delay); });
    }
  }, []);

  const castVideo = useCallback(async (request: CastRequest) => {
    const sdk = sdkRef.current;
    if (sdk === null) return;
    const session = sdk.framework.CastContext.getInstance().getCurrentSession();
    if (session === null) return;

    setError(null);
    setStatus('loading');

    // Replacing what is playing: the previous capability is withdrawn BEFORE a
    // new one is asked for, so one sender never holds two live grants.
    const previous = grantRef.current;
    grantRef.current = null;
    if (previous !== null) {
      void revokeCastGrant(previous.grantId);
    }

    let grant: CastGrant;
    try {
      grant = await acquireGrant(request.fileId);
    } catch (cause) {
      if (!mountedRef.current) return;
      setStatus('error');
      setError(cause instanceof Error && cause.message === 'preparing' ? 'preparing' : 'grant');
      return;
    }

    grantRef.current = grant;
    itemRef.current = { fileId: request.fileId, title: request.title };
    lastPositionRef.current = request.positionSeconds;

    // Origin-relative paths become absolute against THIS page's secure origin.
    // Never a Host header and never a stored base URL: the address the browser
    // is already trusting is the one the television is told to use.
    const origin = window.location.origin;
    const mediaInfo = new sdk.chrome.cast.media.MediaInfo(
      `${origin}${grant.contentPath}`,
      grant.contentType,
    );
    mediaInfo.streamType = sdk.chrome.cast.media.StreamType.BUFFERED;

    const metadata = new sdk.chrome.cast.media.GenericMediaMetadata();
    metadata.title = request.title;
    if (request.subtitle != null && request.subtitle !== '') {
      metadata.subtitle = request.subtitle;
    }
    metadata.images = [new sdk.chrome.cast.Image(`${origin}${grant.posterPath}`)];
    mediaInfo.metadata = metadata;

    const loadRequest = new sdk.chrome.cast.media.LoadRequest(mediaInfo);
    // The television picks up where the browser was, not at the beginning.
    loadRequest.currentTime = Math.max(0, request.positionSeconds);
    loadRequest.autoplay = true;

    try {
      await session.loadMedia(loadRequest);
    } catch {
      if (!mountedRef.current) return;
      // A receiver that refuses the media is almost always a codec it cannot
      // decode. Say so plainly; never quietly offer another way to get the file.
      setStatus('error');
      setError('media');
      return;
    }

    if (!mountedRef.current) return;
    setStatus('playing');
    setSessionState('connected');
    syncRemote();
  }, [acquireGrant, syncRemote]);

  // ── remote control ──────────────────────────────────────────────────────

  const playOrPause = useCallback(() => { controllerRef.current?.playOrPause(); }, []);
  const toggleMute = useCallback(() => { controllerRef.current?.muteOrUnmute(); }, []);

  // The framework's seek/volume are commit-what-is-on-the-player calls: write
  // the field, then tell the controller to send it.
  const seek = useCallback((seconds: number) => {
    const player = playerRef.current;
    if (player === null) return;
    player.currentTime = Math.max(0, seconds);
    controllerRef.current?.seek();
    syncRemote();
  }, [syncRemote]);

  const setVolume = useCallback((level: number) => {
    const player = playerRef.current;
    if (player === null) return;
    player.volumeLevel = Math.min(1, Math.max(0, level));
    controllerRef.current?.setVolumeLevel();
    syncRemote();
  }, [syncRemote]);

  const stopCasting = useCallback(async () => {
    const session =
      sdkRef.current?.framework.CastContext.getInstance().getCurrentSession() ?? null;

    // Stop the picture, withdraw the capability, then drop the session. In that
    // order a slow revoke can never leave the television still playing.
    try {
      session?.getMediaSession?.()?.stop();
    } catch {
      // The receiver may already be gone; teardown still has to run.
    }

    teardown();

    try {
      session?.endSession(true);
    } catch {
      // Same.
    }
    await Promise.resolve();
  }, [teardown]);

  const consumeHandoff = useCallback((fileId: string): number | null => {
    const pending = handoffRef.current;
    if (pending === null || pending.fileId !== fileId) return null;
    handoffRef.current = null;
    return pending.positionSeconds;
  }, []);

  const value = useMemo<CastContextValue>(() => ({
    availability,
    sessionState,
    status,
    error,
    remote,
    castVideo,
    playOrPause,
    seek,
    setVolume,
    toggleMute,
    stopCasting,
    consumeHandoff,
  }), [
    availability, sessionState, status, error, remote,
    castVideo, playOrPause, seek, setVolume, toggleMute, stopCasting, consumeHandoff,
  ]);

  return <CastContext.Provider value={value}>{children}</CastContext.Provider>;
}

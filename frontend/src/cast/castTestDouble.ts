import type {
  CastContextLike,
  CastFrameworkLike,
  CastLoadRequestLike,
  CastMediaInfoLike,
  CastSession,
  ChromeCastLike,
  RemotePlayerControllerLike,
  RemotePlayerLike,
} from './castSdkTypes';

// A working stand-in for the Google Cast Web Sender framework.
//
// There is no way to talk to a real receiver from a test, and emulating the
// network-level Cast protocol would be testing Google's implementation rather
// than ours. What matters here is the SEAM: does NubArca install the readiness
// callback before the script, ask for the Default Media Receiver, mint a grant,
// hand over the local position, mirror receiver-initiated changes, and revoke on
// the way out. Those are all observable against this double.
//
// It is deliberately faithful about the two shapes that are easy to get wrong:
// RemotePlayerController's commit-the-field-then-send calls (seek, volume), and
// ANY_CHANGE being ONE event for every field.

export interface FakeCastSdk {
  framework: CastFrameworkLike;
  chrome: ChromeCastLike;
  player: RemotePlayerLike;
  /** Everything the sender asked the receiver to play, in order. */
  loadRequests: CastLoadRequestLike[];
  /** Options passed to CastContext.setOptions. */
  options: { receiverApplicationId: string; autoJoinPolicy: string } | null;
  /** Times endSession(true) was called. */
  endSessionCalls: number;
  /** Times the media session was stopped. */
  mediaStopCalls: number;
  /** Simulate a receiver being chosen in the Chrome picker. */
  connect(deviceName?: string): void;
  /** Simulate the receiver going away without an explicit stop. */
  disconnect(): void;
  /** Raise the framework's one-for-everything player event. */
  emitPlayerChange(): void;
  /** Drive a receiver-initiated change (TV remote, another sender). */
  receiverUpdate(patch: Partial<RemotePlayerLike>): void;
}

const SESSION_STATE = {
  NO_SESSION: 'NO_SESSION',
  SESSION_STARTING: 'SESSION_STARTING',
  SESSION_STARTED: 'SESSION_STARTED',
  SESSION_START_FAILED: 'SESSION_START_FAILED',
  SESSION_ENDING: 'SESSION_ENDING',
  SESSION_ENDED: 'SESSION_ENDED',
  SESSION_RESUMED: 'SESSION_RESUMED',
} as const;

const CAST_STATE = {
  NO_DEVICES_AVAILABLE: 'NO_DEVICES_AVAILABLE',
  NOT_CONNECTED: 'NOT_CONNECTED',
  CONNECTING: 'CONNECTING',
  CONNECTED: 'CONNECTED',
} as const;

export interface FakeCastOptions {
  /** Make loadMedia reject, as a receiver that cannot decode the media does. */
  rejectLoad?: boolean;
}

export function createFakeCastSdk(options: FakeCastOptions = {}): FakeCastSdk {
  const loadRequests: CastLoadRequestLike[] = [];
  const playerListeners: Array<(event: { field: string; value: unknown }) => void> = [];
  const contextListeners = new Map<string, Array<(event: Record<string, unknown>) => void>>();

  const state = {
    deviceName: 'Test receiver',
    session: null as CastSession | null,
    options: null as { receiverApplicationId: string; autoJoinPolicy: string } | null,
    endSessionCalls: 0,
    mediaStopCalls: 0,
  };

  const player: RemotePlayerLike = {
    isConnected: false,
    isPaused: true,
    isMuted: false,
    currentTime: 0,
    duration: 0,
    volumeLevel: 1,
    mediaInfo: null,
    playerState: null,
  };

  const emitPlayerChange = () => {
    for (const listener of [...playerListeners]) {
      listener({ field: 'anyChange', value: null });
    }
  };

  const emitContext = (event: string, payload: Record<string, unknown>) => {
    for (const listener of [...(contextListeners.get(event) ?? [])]) {
      listener(payload);
    }
  };

  const controller: RemotePlayerControllerLike = {
    addEventListener: (_event, handler) => { playerListeners.push(handler); },
    removeEventListener: (_event, handler) => {
      const index = playerListeners.indexOf(handler);
      if (index >= 0) playerListeners.splice(index, 1);
    },
    playOrPause: () => { player.isPaused = !player.isPaused; emitPlayerChange(); },
    muteOrUnmute: () => { player.isMuted = !player.isMuted; emitPlayerChange(); },
    // Commit-what-is-on-the-player, exactly like the real controller.
    seek: () => { emitPlayerChange(); },
    setVolumeLevel: () => { emitPlayerChange(); },
    stop: () => { player.isPaused = true; emitPlayerChange(); },
  };

  const makeSession = (): CastSession => ({
    getCastDevice: () => ({ friendlyName: state.deviceName }),
    loadMedia: async (request: CastLoadRequestLike) => {
      if (options.rejectLoad === true) throw new Error('receiver refused the media');
      loadRequests.push(request);
      player.mediaInfo = request.media;
      player.isConnected = true;
      player.isPaused = false;
      player.currentTime = request.currentTime ?? 0;
      player.duration = 600;
      emitPlayerChange();
      return undefined;
    },
    getMediaSession: () => ({
      stop: () => { state.mediaStopCalls += 1; },
    }),
    endSession: () => {
      state.endSessionCalls += 1;
      state.session = null;
      player.isConnected = false;
      emitPlayerChange();
      emitContext('SESSION_STATE_CHANGED', { sessionState: SESSION_STATE.SESSION_ENDED });
    },
  });

  const context: CastContextLike = {
    setOptions: (next) => { state.options = next; },
    getCurrentSession: () => state.session,
    getCastState: () =>
      state.session === null ? CAST_STATE.NOT_CONNECTED : CAST_STATE.CONNECTED,
    addEventListener: (event, handler) => {
      const list = contextListeners.get(event) ?? [];
      list.push(handler);
      contextListeners.set(event, list);
    },
    removeEventListener: (event, handler) => {
      const list = contextListeners.get(event) ?? [];
      const index = list.indexOf(handler);
      if (index >= 0) list.splice(index, 1);
    },
  };

  const framework: CastFrameworkLike = {
    CastContext: { getInstance: () => context },
    RemotePlayer: function RemotePlayerStub(this: RemotePlayerLike) {
      return player;
    } as unknown as new () => RemotePlayerLike,
    RemotePlayerController: function ControllerStub(this: RemotePlayerControllerLike) {
      return controller;
    } as unknown as new (p: RemotePlayerLike) => RemotePlayerControllerLike,
    CastContextEventType: {
      CAST_STATE_CHANGED: 'CAST_STATE_CHANGED',
      SESSION_STATE_CHANGED: 'SESSION_STATE_CHANGED',
    },
    RemotePlayerEventType: { ANY_CHANGE: 'ANY_CHANGE' },
    CastState: CAST_STATE,
    SessionState: SESSION_STATE,
  };

  const chrome: ChromeCastLike = {
    cast: {
      media: {
        DEFAULT_MEDIA_RECEIVER_APP_ID: 'CC1AD845',
        MediaInfo: function MediaInfoStub(
          this: CastMediaInfoLike, contentId: string, contentType: string,
        ) {
          this.contentId = contentId;
          this.contentType = contentType;
          this.streamType = 'BUFFERED';
        } as unknown as new (contentId: string, contentType: string) => CastMediaInfoLike,
        GenericMediaMetadata:
          function MetadataStub(this: Record<string, unknown>) {} as unknown as
            new () => Record<string, unknown>,
        LoadRequest: function LoadRequestStub(
          this: CastLoadRequestLike, media: CastMediaInfoLike,
        ) {
          this.media = media;
        } as unknown as new (media: CastMediaInfoLike) => CastLoadRequestLike,
        StreamType: { BUFFERED: 'BUFFERED' },
      },
      AutoJoinPolicy: {
        ORIGIN_SCOPED: 'origin_scoped',
        TAB_AND_ORIGIN_SCOPED: 'tab_and_origin_scoped',
        PAGE_SCOPED: 'page_scoped',
      },
      Image: function ImageStub(this: { url: string }, url: string) {
        this.url = url;
      } as unknown as new (url: string) => { url: string },
    },
  };

  return {
    framework,
    chrome,
    player,
    loadRequests,
    get options() { return state.options; },
    get endSessionCalls() { return state.endSessionCalls; },
    get mediaStopCalls() { return state.mediaStopCalls; },
    connect: (deviceName = 'Test receiver') => {
      state.deviceName = deviceName;
      state.session = makeSession();
      player.isConnected = true;
      emitContext('SESSION_STATE_CHANGED', { sessionState: SESSION_STATE.SESSION_STARTED });
    },
    disconnect: () => {
      state.session = null;
      player.isConnected = false;
      emitPlayerChange();
    },
    emitPlayerChange,
    receiverUpdate: (patch) => {
      Object.assign(player, patch);
      emitPlayerChange();
    },
  };
}

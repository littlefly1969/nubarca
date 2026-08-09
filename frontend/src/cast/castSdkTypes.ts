// The slice of the Google Cast Web Sender API NubArca actually uses.
//
// Hand-written rather than pulled from `@types/chromecast-caf-sender`: the
// surface used here is about twenty members, the real SDK is a global loaded at
// runtime that no bundler can check anyway, and a narrow local declaration is
// the honest description of what this code depends on. It also means the test
// double and the production types are the same shape by construction.
//
// Everything is optional at the top level because the SDK may never load at
// all — an unsupported browser, a blocked script, an offline machine.

export const CAST_SENDER_SCRIPT =
  'https://www.gstatic.com/cv/js/sender/v1/cast_sender.js?loadCastFramework=1';

export interface CastMediaInfoLike {
  contentId: string;
  contentType: string;
  streamType: string;
  metadata?: unknown;
  customData?: unknown;
  // How the receiver should parse HLS segments. NubArca's ladder is fMP4/CMAF
  // (init.mp4 + fragmented MP4 media segments), and a Web Receiver told nothing
  // assumes MPEG-2 TS — it then buffers forever on a stream it cannot parse,
  // showing a loading UI rather than an error. Both are set: the audio/general
  // property and the video-specific one.
  hlsSegmentFormat?: string;
  hlsVideoSegmentFormat?: string;
}

export interface CastLoadRequestLike {
  media: CastMediaInfoLike;
  currentTime?: number;
  autoplay?: boolean;
}

export interface CastMediaSession {
  /** Ends playback on the receiver. */
  stop(successCallback?: () => void, errorCallback?: (error: unknown) => void): void;
}

export interface CastSession {
  getCastDevice?(): { friendlyName?: string } | undefined;
  getSessionObj?(): unknown;
  loadMedia(request: CastLoadRequestLike): Promise<unknown>;
  getMediaSession?(): CastMediaSession | null;
  endSession(stopCasting: boolean): void;
}

export interface RemotePlayerLike {
  isConnected: boolean;
  isPaused: boolean;
  isMuted: boolean;
  currentTime: number;
  duration: number;
  volumeLevel: number;
  mediaInfo: CastMediaInfoLike | null;
  playerState: string | null;
}

export interface RemotePlayerControllerLike {
  addEventListener(event: string, handler: (event: { field: string; value: unknown }) => void): void;
  removeEventListener(
    event: string, handler: (event: { field: string; value: unknown }) => void): void;
  playOrPause(): void;
  muteOrUnmute(): void;
  seek(): void;
  setVolumeLevel(): void;
  stop(): void;
}

export interface CastContextLike {
  setOptions(options: { receiverApplicationId: string; autoJoinPolicy: string }): void;
  getCurrentSession(): CastSession | null;
  getCastState(): string;
  addEventListener(event: string, handler: (event: Record<string, unknown>) => void): void;
  removeEventListener(event: string, handler: (event: Record<string, unknown>) => void): void;
}

export interface CastFrameworkLike {
  CastContext: { getInstance(): CastContextLike };
  RemotePlayer: new () => RemotePlayerLike;
  RemotePlayerController: new (player: RemotePlayerLike) => RemotePlayerControllerLike;
  CastContextEventType: { CAST_STATE_CHANGED: string; SESSION_STATE_CHANGED: string };
  RemotePlayerEventType: { ANY_CHANGE: string };
  CastState: {
    NO_DEVICES_AVAILABLE: string;
    NOT_CONNECTED: string;
    CONNECTING: string;
    CONNECTED: string;
  };
  SessionState: {
    NO_SESSION: string;
    SESSION_STARTING: string;
    SESSION_STARTED: string;
    SESSION_START_FAILED: string;
    SESSION_ENDING: string;
    SESSION_ENDED: string;
    SESSION_RESUMED: string;
  };
}

export interface ChromeCastLike {
  cast: {
    media: {
      DEFAULT_MEDIA_RECEIVER_APP_ID: string;
      MediaInfo: new (contentId: string, contentType: string) => CastMediaInfoLike;
      GenericMediaMetadata: new () => Record<string, unknown>;
      LoadRequest: new (media: CastMediaInfoLike) => CastLoadRequestLike;
      StreamType: { BUFFERED: string };
      HlsSegmentFormat: { FMP4: string };
      HlsVideoSegmentFormat: { FMP4: string };
    };
    AutoJoinPolicy: { ORIGIN_SCOPED: string; TAB_AND_ORIGIN_SCOPED: string; PAGE_SCOPED: string };
    Image: new (url: string) => { url: string };
  };
}

// React 19 removed the ambient global JSX namespace: intrinsic elements are
// declared by augmenting React's own.
declare module 'react' {
  namespace JSX {
    interface IntrinsicElements {
      /**
       * The OFFICIAL Cast launcher, a custom element the framework registers.
       * Used rather than an imitation icon: it is the affordance users already
       * recognise, and the framework — not NubArca — owns its availability, its
       * connected/disconnected art and its device-picker behaviour.
       */
      'google-cast-launcher': React.DetailedHTMLProps<
        React.HTMLAttributes<HTMLElement>, HTMLElement>;
    }
  }
}

declare global {
  interface Window {
    /**
     * The SDK calls this the moment it finishes loading. It has to EXIST before
     * the script tag is appended — the SDK reads it synchronously on load and a
     * callback installed afterwards is simply never invoked.
     */
    __onGCastApiAvailable?: (available: boolean, reason?: string) => void;
    /** The SDK's global namespace; `framework` is what this code speaks to. */
    cast?: { framework?: CastFrameworkLike };
    chrome?: ChromeCastLike;
  }
}

export {};

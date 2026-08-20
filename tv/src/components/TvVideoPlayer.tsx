import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';
import { AppState, StyleSheet, Text, View, type AppStateStatus } from 'react-native';
import { VideoView, useVideoPlayer, type VideoSource } from 'expo-video';
import { getTvMediaHeaders, resolveTvMediaUrl } from '../api/client';
import { probeTvVideo, type TvVideoMode } from '../video/probe';
import { beginVideoRotation, onVideoEnded, onVideoProgress } from '../lib/partySlideshow';
import { VIDEO_SEEK_SECONDS } from '../video/remoteMap';
import { SlideImage } from './SlideImage';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';
import { subscribeOutputLost } from '../lib/tvPlatform';
import {
  releasesPlayer,
  restorablePosition,
  resumesFromBackground,
  shouldAutoResume,
  shouldMountPlayer,
  hostStateFromAppState,
  type HostState,
  type PlaybackSnapshot,
} from '../video/playerLifecycle';

// Full-screen video playback for the TV viewer.
//
// The /video endpoint speaks two contracts (see src/video/probe.ts); a 1-byte
// probe decides how this component behaves:
//   preparing → poster + status pill, re-probing every 5 s (each probe is also
//               the server's idempotent lazy re-enqueue of the transcode job)
//   hls/direct → expo-video (ExoPlayer) with the explicit contentType hint —
//               ExoPlayer cannot infer HLS from an extension-less /video URL
//   error     → poster + error pill (the item is still navigable)
//
// The REMOTE stays owned by the viewer screen: this component only exposes
// imperative controls through `controlsRef` and reports `onEnded`. The TV
// session cookie rides on the native player request explicitly — the native
// loader does not share RN fetch's cookie jar.
//
// ── RESOURCE DISCIPLINE ────────────────────────────────────────────────────
// A Fire Stick is a constrained streaming device. Three rules, all visible
// below, keep playback inside a sane budget and keep a decoder or network
// failure from taking the whole app down:
//
//  1. EXACTLY ONE PLAYER. `ReadyPlayer` is keyed by source, so changing item
//     UNMOUNTS the old player before the new one mounts; expo-video releases the
//     native ExoPlayer on unmount. Nothing pre-creates a player for the next or
//     previous item — poster prefetch is fine, player prefetch is not, because
//     two live players mean two decoder sessions and, briefly, two audio
//     streams.
//  2. BOUNDED BUFFER. Android's default is an unlimited BYTE budget; on a
//     high-bitrate source that is a memory climb with no ceiling. The explicit
//     `bufferOptions` below replaces it with a duration AND byte bound.
//  3. LIFECYCLE. A genuine BACKGROUND transition snapshots the position and
//     then UNMOUNTS the player component, which is what releases the native
//     ExoPlayer, its decoder, the Media3 MediaSession and the audio-focus
//     registration — none of which NubArca owns. A transient 'inactive' blip
//     only PAUSES: an incidental focus change is not an Activity stop, and
//     re-preparing ExoPlayer for one would churn the decoder for nothing.
//     Returning recreates exactly one player, restores the position when it
//     belongs to the same source, and does NOT auto-resume. See
//     video/playerLifecycle.ts for the rules and the AppState effect below for
//     the wiring.
//
//     This comment used to say "pauses and releases" while the code only
//     paused. It now describes what executes.
//
// A decoder/network error is reported through `statusChange` and turns into the
// poster + a generic message. It never propagates, and nothing here can
// terminate the process.

export interface TvVideoControls {
  togglePlay(): void;
  seekBy(seconds: number): void;
  // Stop and release before the app closes or the screen goes away, so nothing
  // keeps playing behind the launcher.
  stop(): void;
  // Position + intent, read by the PARENT immediately before it unmounts this
  // player for a background transition. It has to be pulled from outside
  // because the component that owns the position is the one being torn down.
  snapshot(): PlaybackSnapshot | null;
}

const PREPARING_POLL_MS = 5000;

// Buffer budget for a 1080p Fire TV target. Chosen deliberately, not copied:
//
//   preferredForwardBufferDuration 20 s  — expo-video's own Android default.
//     Keeping it is the point: it is enough to ride out a household Wi-Fi dip
//     on an HLS ladder, and raising it buys rebuffer resilience the device
//     cannot pay for in RAM.
//   minBufferForPlayback 2 s  — the default. Lowering it starts sooner and
//     stalls more; raising it delays every start.
//   maxBufferBytes 24 MiB  — the value that actually changes behaviour. The
//     default (0) means UNLIMITED: at 20 s of a 40 Mbit/s source that is ~100 MB
//     of buffer alone, against a foreground budget meant to stay comfortably
//     under 300 MB. 24 MiB covers 20 s at ~10 Mbit/s (the top of the HLS ladder
//     this app plays) and simply caps the duration target on anything richer.
//   prioritizeTimeOverSizeThreshold false — with a byte cap in place, honouring
//     the cap is the point; flipping this would let duration win and re-open the
//     ceiling.
//
// These are a starting budget to be confirmed against a physical device with
// Fire TV's Advanced Options → VIDEO/memory overlays; the numbers are here, in
// one place, so a measurement can move them.
const BUFFER_OPTIONS = {
  preferredForwardBufferDuration: 20,
  minBufferForPlayback: 2,
  maxBufferBytes: 24 * 1024 * 1024,
  prioritizeTimeOverSizeThreshold: false,
} as const;

interface Props {
  // /api/tv/media/{id}/video or /api/tv/personal/media/{id}/video.
  videoPath: string;
  // Poster path shown while probing/preparing/on error; may be null.
  posterPath: string | null;
  onEnded: () => void;
  controlsRef: MutableRefObject<TvVideoControls | null>;
  personal?: boolean;
  // Party slideshow cap, in seconds of PLAYBACK. null (the default) means play
  // to the natural end, which is every non-party slideshow.
  maxPlaybackSeconds?: number | null;
  // Fired once when the cap is reached. The viewer owns what "advance" means;
  // this component only reports that the cap was crossed.
  onCapReached?: () => void;
  // Slideshow play state. When provided, it GOVERNS the player: a paused
  // slideshow must not have audio coming out of it, and the historical
  // unconditional play() on mount would have done exactly that. Undefined keeps
  // the manual viewer's autoplay-on-open behaviour untouched.
  playing?: boolean;
  // Typed readiness for the viewer's preparing-grace policy. The POLICY lives
  // in the viewer; this only reports the state.
  onReadyStateChange?: (state: TvVideoReadyState) => void;
}

// What the viewer needs to know about playability, and nothing more.
export type TvVideoReadyState = 'probing' | 'preparing' | 'ready' | 'error';

export function TvVideoPlayer({
  videoPath, posterPath, onEnded, controlsRef, personal = false,
  maxPlaybackSeconds = null, onCapReached, playing, onReadyStateChange,
}: Props) {
  const { t } = useI18n();
  // BACKGROUND RELEASE. The player component is UNMOUNTED when the app is
  // genuinely backgrounded, and expo-video's documented contract is that
  // useVideoPlayer releases its native player on unmount. That single move
  // releases the ExoPlayer, its decoder, the Media3 MediaSession and the
  // audio-focus registration — none of which NubArca owns, and none of which it
  // therefore has to release by hand.
  //
  // 'inactive' is deliberately NOT a release. React Native reports it for brief
  // interruptions that are not an Activity stop, and tearing ExoPlayer down for
  // those would churn the decoder on incidental focus changes.
  // DERIVED, not assumed. A player created while Android is already backgrounded
  // used to start at 'active' and could mount — and autoplay — for the interval
  // before the first AppState event arrived.
  const [host, setHost] = useState<HostState>(
    () => hostStateFromAppState(AppState.currentState));
  const snapshotRef = useRef<PlaybackSnapshot | null>(null);
  const controlsForSnapshot = controlsRef;
  const [mode, setMode] = useState<TvVideoMode | 'probing'>('probing');

  // Report readiness upward so the viewer can run its grace window. 'hls' and
  // 'direct' both mean "a player can be mounted", which is what ready means here.
  useEffect(() => {
    if (!onReadyStateChange) return;
    onReadyStateChange(
      mode === 'hls' || mode === 'direct' ? 'ready'
        : mode === 'preparing' ? 'preparing'
          : mode === 'error' ? 'error' : 'probing',
    );
  }, [mode, onReadyStateChange]);

  // Probe on mount / item change; keep polling while the ladder is prepared.
  useEffect(() => {
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | null = null;
    setMode('probing');

    const probe = async () => {
      const result = await probeTvVideo(videoPath, personal);
      if (cancelled) return;
      tvDebug('video', 'probe', result);
      setMode(result);
      if (result === 'preparing') {
        timer = setTimeout(() => { void probe(); }, PREPARING_POLL_MS);
      }
    };
    void probe();

    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [videoPath, personal]);

  // Snapshot before the unmount, restore after the remount. The parent holds
  // both because the child is the thing being released.
  useEffect(() => {
    let previous: HostState = hostStateFromAppState(AppState.currentState);
    const subscription = AppState.addEventListener('change', (next: AppStateStatus) => {
      const state = hostStateFromAppState(next);
      if (releasesPlayer(previous, state)) {
        // Pull position and intent out of the live player FIRST; a moment later
        // it will not exist.
        snapshotRef.current = controlsForSnapshot.current?.snapshot() ?? null;
        tvDebug('video', 'lifecycle', 'releasing-on-background');
      } else if (resumesFromBackground(previous, state)) {
        tvDebug('video', 'lifecycle', 'foregrounded');
      }
      previous = state;
      setHost(state);
    });
    return () => subscription.remove();
  }, [controlsForSnapshot]);

  if ((mode === 'hls' || mode === 'direct') && shouldMountPlayer(host)) {
    return (
      <ReadyPlayer
        // KEY: a new video path is a NEW component instance, so the previous
        // player unmounts (and expo-video releases its native ExoPlayer) before
        // this one is created. Without it, `useVideoPlayer` would swap the
        // source on a live player and the old decoder session could overlap the
        // new one — two audible streams for a frame, and two codec sessions on a
        // device that has very few.
        key={videoPath}
        videoPath={videoPath}
        mode={mode}
        onEnded={onEnded}
        controlsRef={controlsRef}
        personal={personal}
        maxPlaybackSeconds={maxPlaybackSeconds}
        onCapReached={onCapReached}
        playing={playing}
        onFatalError={() => setMode('error')}
        restoreSnapshot={snapshotRef.current}
        host={host}
      />
    );
  }

  return (
    <View style={styles.fill}>
      <SlideImage path={posterPath} personal={personal} />
      {mode === 'preparing' && (
        <View style={styles.pill}><Text style={styles.pillText}>{t('viewer.videoPreparing')}</Text></View>
      )}
      {mode === 'error' && (
        <View style={styles.pill}><Text style={styles.pillTextError}>{t('viewer.videoError')}</Text></View>
      )}
    </View>
  );
}

// Mounted only once the mode is known, so useVideoPlayer gets its real source
// immediately (no null-source replace dance), and remounted per video so there
// is never more than one live native player.
function ReadyPlayer({
  videoPath, mode, onEnded, controlsRef, onFatalError, personal,
  maxPlaybackSeconds, onCapReached, playing, restoreSnapshot = null, host,
}: {
  videoPath: string;
  mode: 'hls' | 'direct';
  onEnded: () => void;
  controlsRef: MutableRefObject<TvVideoControls | null>;
  onFatalError: () => void;
  // Position captured before a background release, restored on the recreated
  // player when — and only when — it belongs to THIS source.
  restoreSnapshot?: PlaybackSnapshot | null;
  // Only ever 'active' or 'inactive' here: a 'background' host means this
  // component is not rendered at all.
  host: HostState;
  personal: boolean;
  maxPlaybackSeconds: number | null;
  onCapReached?: () => void;
  playing?: boolean;
}) {
  const source: VideoSource = {
    uri: resolveTvMediaUrl(videoPath),
    headers: getTvMediaHeaders(personal),
    contentType: mode === 'hls' ? 'hls' : 'progressive',
  };
  const capSeconds = maxPlaybackSeconds;
  const player = useVideoPlayer(source, (p) => {
    p.loop = false;
    // Periodic time updates ONLY when a cap is in force. The cap is measured in
    // the video's own clock, so it needs the player's position rather than a
    // timer — and a slideshow with no cap should not pay for the events.
    p.timeUpdateEventInterval = capSeconds === null ? 0 : 0.5;
    // Replace Android's unlimited byte budget before playback starts.
    p.bufferOptions = BUFFER_OPTIONS;
    // Make the product invariant VISIBLE and regression-testable rather than
    // relying on an upstream default: expo-video owns the video keep-awake, and
    // NubArca adds no second lock on top of it.
    p.keepScreenOnWhilePlaying = true;

    // Restore a position captured before a background release. `restorablePosition`
    // refuses a snapshot from a DIFFERENT source, so changing item while
    // backgrounded can never seek the new video to the old one's timestamp.
    const resumeAt = restorablePosition(restoreSnapshot, videoPath);
    if (resumeAt !== null) p.currentTime = resumeAt;

    // After a real background interruption playback stays PAUSED: returning to a
    // room and having audio start by itself is the behaviour this product
    // deliberately avoids, and shouldAutoResume() says so in one place.
    const interrupted = resumeAt !== null;
    if (playing !== false && (!interrupted || shouldAutoResume())) p.play();
  });

  // Whether the user has playback going. Consulted when returning from the
  // background so a paused video is not un-paused behind their back.
  const wasPlayingRef = useRef(true);

  // Imperative remote controls for the viewer. Cleared on unmount so a stale
  // ref can never reach a released player.
  useEffect(() => {
    controlsRef.current = {
      togglePlay: () => {
        if (player.playing) {
          player.pause();
          wasPlayingRef.current = false;
        } else {
          player.play();
          wasPlayingRef.current = true;
        }
      },
      seekBy: (seconds: number) => {
        player.seekBy(seconds);
      },
      stop: () => {
        try {
          player.pause();
        } catch {
          // Already released — stopping is best-effort by definition.
        }
        wasPlayingRef.current = false;
      },
      snapshot: () => {
        try {
          return {
            source: videoPath,
            positionSeconds: player.currentTime,
            wasPlaying: player.playing,
          };
        } catch {
          // Released already: there is nothing to restore, and guessing a
          // position would be worse than starting from the beginning.
          return null;
        }
      },
    };
    return () => { controlsRef.current = null; };
  }, [player, controlsRef, videoPath]);

  // OUTPUT ROUTE. HDMI unplugged, a receiver switched away, a Bluetooth speaker
  // gone: pause and keep the position. Never navigate, never close the viewer,
  // never reset — the user has not asked to stop watching, only the sound has
  // nowhere to go. Coming back does NOT auto-resume, for the same reason
  // returning from background does not.
  //
  // Subscribed only while a player exists, so outside a playback context
  // nothing is registered natively.
  useEffect(() => subscribeOutputLost(() => {
    try {
      wasPlayingRef.current = player.playing;
      player.pause();
    } catch {
      // Already released; nothing to pause.
    }
    tvDebug('video', 'lifecycle', 'paused-on-output-lost');
  }), [player]);

  // The PARENT owns background/foreground: it snapshots the position and then
  // unmounts this component, which is what releases the native player. All this
  // effect does is honour a transient 'inactive' blip by PAUSING — never by
  // releasing, because an incidental focus change is not an Activity stop and
  // re-preparing ExoPlayer for one would churn the decoder for nothing.
  useEffect(() => {
    if (host === 'active') return;
    try {
      wasPlayingRef.current = player.playing;
      player.pause();
    } catch {
      // Already released; nothing to pause.
    }
    tvDebug('video', 'lifecycle', 'paused-on-inactive');
  }, [host, player]);


  const reportError = useCallback((message: string | undefined) => {
    // Sanitized: the CATEGORY reaches the debug log, the user sees a plain
    // sentence, and no MediaCodec/Java exception text is ever shown.
    tvDebug('video', 'player-error', classifyPlayerError(message));
    onFatalError();
  }, [onFatalError]);

  // The event interval is set in the player initializer from the cap known at
  // mount. Party mode can be switched on mid-video, so keep it in sync — without
  // this the latch below would be subscribed to a stream that never fires.
  useEffect(() => {
    try {
      player.timeUpdateEventInterval = capSeconds === null ? 0 : 0.5;
    } catch {
      // Released mid-update; the next mount sets it from the initializer.
    }
  }, [player, capSeconds]);

  // ONE latch decides whether this video may advance the slideshow, so a cap
  // crossing and a natural end on the same frame cannot advance twice. The
  // rules are the pure, tested ones in lib/partySlideshow.
  const rotationRef = useRef(beginVideoRotation(capSeconds));
  useEffect(() => { rotationRef.current = beginVideoRotation(capSeconds); }, [capSeconds]);

  useEffect(() => {
    const ended = player.addListener('playToEnd', () => {
      const step = onVideoEnded(rotationRef.current);
      rotationRef.current = step.state;
      if (step.advance) onEnded();
    });
    const status = player.addListener('statusChange', ({ status: s, error }) => {
      if (s === 'error') reportError(error?.message);
    });
    // Only subscribed when a cap exists. `currentTime` is the video's own clock:
    // paused and buffering time never arrives, so it cannot consume the cap.
    const progress = capSeconds === null ? null : player.addListener(
      'timeUpdate',
      ({ currentTime }) => {
        const step = onVideoProgress(rotationRef.current, currentTime);
        rotationRef.current = step.state;
        if (step.advance) (onCapReached ?? onEnded)();
      },
    );
    return () => {
      ended.remove();
      status.remove();
      progress?.remove();
    };
  }, [player, onEnded, onCapReached, reportError, capSeconds]);

  // The slideshow's play state GOVERNS the player while the viewer supplies
  // one. Without this the pill could read PAUSED while audio kept playing.
  useEffect(() => {
    if (playing === undefined) return;
    try {
      if (playing && !player.playing) player.play();
      else if (!playing && player.playing) player.pause();
      wasPlayingRef.current = playing;
    } catch {
      // Released mid-transition; the next mount starts from the correct state.
    }
  }, [playing, player]);

  // Belt-and-braces release: expo-video releases the native player when the
  // hook unmounts, but pausing first guarantees no audio survives the frame in
  // which the viewer closes or the app finishes its task.
  useEffect(() => () => {
    try {
      player.pause();
    } catch {
      // Already released.
    }
  }, [player]);

  return (
    <VideoView
      style={styles.fill}
      player={player}
      nativeControls={false}
      contentFit="contain"
    />
  );
}

// Coarse, non-identifying buckets for the debug log. Never the raw message: a
// decoder exception can carry a file path or a URL with a token in it.
function classifyPlayerError(message: string | undefined): string {
  if (!message) return 'unknown';
  const text = message.toLowerCase();
  if (text.includes('decoder') || text.includes('codec')) return 'decoder';
  if (text.includes('source') || text.includes('format') || text.includes('container')) return 'source';
  if (text.includes('http') || text.includes('network') || text.includes('connect')) return 'network';
  if (text.includes('memory')) return 'memory';
  return 'other';
}

// Default relative-seek step, re-exported for the viewer's convenience.
export const TV_VIDEO_SEEK_SECONDS = VIDEO_SEEK_SECONDS;

const styles = StyleSheet.create({
  fill: { flex: 1, alignSelf: 'stretch' },
  pill: {
    position: 'absolute',
    bottom: '12%',
    alignSelf: 'center',
    paddingHorizontal: 18,
    paddingVertical: 8,
    borderRadius: 999,
    backgroundColor: 'rgba(0,0,0,0.78)',
  },
  pillText: { color: '#fff', fontSize: 24, fontWeight: '700' },
  pillTextError: { color: '#ffb4a9', fontSize: 24, fontWeight: '700' },
});

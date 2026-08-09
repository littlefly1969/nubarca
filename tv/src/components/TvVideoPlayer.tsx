import { useCallback, useEffect, useRef, useState, type MutableRefObject } from 'react';
import { AppState, StyleSheet, Text, View, type AppStateStatus } from 'react-native';
import { VideoView, useVideoPlayer, type VideoSource } from 'expo-video';
import { getTvMediaHeaders, resolveTvMediaUrl } from '../api/client';
import { probeTvVideo, type TvVideoMode } from '../video/probe';
import { VIDEO_SEEK_SECONDS } from '../video/remoteMap';
import { SlideImage } from './SlideImage';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';

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
//  3. LIFECYCLE. Backgrounding pauses and releases; returning does NOT
//     auto-resume. See the AppState effect.
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
}

export function TvVideoPlayer({
  videoPath, posterPath, onEnded, controlsRef, personal = false,
}: Props) {
  const { t } = useI18n();
  const [mode, setMode] = useState<TvVideoMode | 'probing'>('probing');

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

  if (mode === 'hls' || mode === 'direct') {
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
        onFatalError={() => setMode('error')}
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
function ReadyPlayer({ videoPath, mode, onEnded, controlsRef, onFatalError, personal }: {
  videoPath: string;
  mode: 'hls' | 'direct';
  onEnded: () => void;
  controlsRef: MutableRefObject<TvVideoControls | null>;
  onFatalError: () => void;
  personal: boolean;
}) {
  const source: VideoSource = {
    uri: resolveTvMediaUrl(videoPath),
    headers: getTvMediaHeaders(personal),
    contentType: mode === 'hls' ? 'hls' : 'progressive',
  };
  const player = useVideoPlayer(source, (p) => {
    p.loop = false;
    p.timeUpdateEventInterval = 0; // no periodic events needed
    // Replace Android's unlimited byte budget before playback starts.
    p.bufferOptions = BUFFER_OPTIONS;
    p.play();
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
    };
    return () => { controlsRef.current = null; };
  }, [player, controlsRef]);

  // Explicit lifecycle. Fire TV backgrounds this app for Home, the Alexa/voice
  // overlay, an app switch and screen sleep, and audio focus is lost with it.
  //
  // Going away: PAUSE. The playback position stays on the player, so the user
  // returns where they left off. Deliberately not a release: expo-video/Media3
  // already stops decoding while paused and in the background, and tearing the
  // player down here would lose the position and force a full re-prepare on
  // every incidental interruption.
  //
  // Coming back: do NOT auto-resume. Returning to a room and having audio start
  // by itself is the behaviour to avoid; the user presses play. The one
  // exception is a brief 'inactive' blip that never became a real background —
  // there was no interruption to recover from.
  useEffect(() => {
    let previous: AppStateStatus = AppState.currentState;
    const subscription = AppState.addEventListener('change', (next) => {
      const leaving = next === 'background' || next === 'inactive';
      if (leaving && previous === 'active') {
        wasPlayingRef.current = player.playing;
        try {
          player.pause();
        } catch {
          // The player may already be gone if the screen is unmounting.
        }
        tvDebug('video', 'lifecycle', 'paused-on-background');
      } else if (next === 'active' && previous === 'background') {
        tvDebug('video', 'lifecycle', 'foregrounded', wasPlayingRef.current ? 'was-playing' : 'was-paused');
      }
      previous = next;
    });
    return () => subscription.remove();
  }, [player]);

  const reportError = useCallback((message: string | undefined) => {
    // Sanitized: the CATEGORY reaches the debug log, the user sees a plain
    // sentence, and no MediaCodec/Java exception text is ever shown.
    tvDebug('video', 'player-error', classifyPlayerError(message));
    onFatalError();
  }, [onFatalError]);

  useEffect(() => {
    const ended = player.addListener('playToEnd', onEnded);
    const status = player.addListener('statusChange', ({ status: s, error }) => {
      if (s === 'error') reportError(error?.message);
    });
    return () => {
      ended.remove();
      status.remove();
    };
  }, [player, onEnded, reportError]);

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

import { useEffect, useState, type MutableRefObject } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import { VideoView, useVideoPlayer, type VideoSource } from 'expo-video';
import { getTvMediaHeaders, resolveTvMediaUrl } from '../api/client';
import { probeTvVideo, type TvVideoMode } from '../video/probe';
import { VIDEO_SEEK_SECONDS } from '../video/remoteMap';
import { SlideImage } from './SlideImage';
import { useI18n } from '../i18n';
import { tvDebug } from '../debug';

// Video-hls slice 4: full-screen video playback for the TV viewer.
//
// The /video endpoint speaks two contracts (see src/video/probe.ts); a 1-byte
// probe decides how this component behaves:
//   preparing → poster + status pill, re-probing every 5 s (each probe is also
//               the server's idempotent lazy re-enqueue of the transcode job)
//   hls/direct → expo-video (ExoPlayer) with the explicit contentType hint —
//               ExoPlayer cannot infer HLS from an extension-less /video URL
//   error     → poster + error pill (the item is still navigable)
//
// The REMOTE stays owned by ViewerScreen: this component only exposes
// imperative controls through `controlsRef` (play/pause + relative seek) and
// reports `onEnded` so the slideshow can auto-advance. The TV session cookie
// rides on the native player request explicitly — the native loader does not
// share RN fetch's cookie jar.

export interface TvVideoControls {
  togglePlay(): void;
  seekBy(seconds: number): void;
}

const PREPARING_POLL_MS = 5000;

interface Props {
  // /api/tv/media/{id}/video (relative TV path from TvAlbumItem.videoUrl).
  videoPath: string;
  // Poster path shown while probing/preparing/on error; may be null.
  posterPath: string | null;
  onEnded: () => void;
  controlsRef: MutableRefObject<TvVideoControls | null>;
  personal?: boolean;
}

export function TvVideoPlayer({ videoPath, posterPath, onEnded, controlsRef, personal = false }: Props) {
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
// immediately (no null-source replace dance).
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
    p.play();
  });

  // Imperative remote controls for ViewerScreen. Cleared on unmount so a stale
  // ref can never reach a released player.
  useEffect(() => {
    controlsRef.current = {
      togglePlay: () => {
        if (player.playing) player.pause();
        else player.play();
      },
      seekBy: (seconds: number) => {
        player.seekBy(seconds);
      },
    };
    return () => { controlsRef.current = null; };
  }, [player, controlsRef]);

  useEffect(() => {
    const ended = player.addListener('playToEnd', onEnded);
    const status = player.addListener('statusChange', ({ status: s, error }) => {
      if (s === 'error') {
        tvDebug('video', 'player-error', error?.message ?? 'unknown');
        onFatalError();
      }
    });
    return () => {
      ended.remove();
      status.remove();
    };
  }, [player, onEnded, onFatalError]);

  return (
    <VideoView
      style={styles.fill}
      player={player}
      nativeControls={false}
      contentFit="contain"
    />
  );
}

// Default relative-seek step, re-exported for ViewerScreen's convenience.
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

// VideoSlide: one video in the viewer pager, driven by an explicit `active`
// flag from the parent.
//
// LIFECYCLE CONTRACT (acceptance BLOCKER):
//   * mounting does NOT autoplay — the pager keeps neighbor slides mounted;
//   * active=true starts playback, active=false pauses it;
//   * unmount always stops the audio and releases the player;
//   * keep-awake is held only by the ACTIVE playing video.
// Two audios can therefore never play at once: only the focused slide is ever
// allowed to reach the playing state.

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import {
  useVideoPlayer,
  VideoView,
  type VideoPlayer,
} from 'expo-video';
import { activateKeepAwakeAsync, deactivateKeepAwake } from 'expo-keep-awake';
import { AuthedImage } from './AuthedImage';
import { ErrorState, LoadingState } from '../ui/states';
import { useI18n } from '../i18n';
import {
  VIDEO_PROBE_MAX_ATTEMPTS,
  VIDEO_PROBE_RETRY_MS,
  probeVideoSource,
  type VideoContainer,
  type VideoProbeFetch,
} from '../media/videoProbe';
import type { ViewerSlide } from '../media/viewerSequence';

type ProbeState = 'probing' | 'ready' | 'preparing' | 'unavailable';

export function VideoSlide({
  slide,
  active,
}: {
  slide: ViewerSlide;
  active: boolean;
}): React.JSX.Element {
  const { t } = useI18n();
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const activeRef = useRef(active);
  activeRef.current = active;

  // The source arrives FULLY BUILT on the slide (uri + cookie snapshot), so a
  // re-render can never hand useVideoPlayer a new object identity.
  const source = slide.videoSource;

  // BOUNDED RANGE PROBE (acceptance contract fix — see media/videoProbe.ts):
  // sends Range: bytes=0-0 with the session cookie, aborts each attempt as
  // soon as the response head is known, and classifies per NubArca contracts
  // (202 preparing / 404 unavailable / 206+video progressive / 200 HLS MIME
  // → hls). The outcome resolves BOTH availability and container; the
  // expo-video player mounts only on a confirmed ready.
  const [probeState, setProbeState] = useState<ProbeState>(
    slide.videoSource ? 'probing' : 'unavailable',
  );

  useEffect(() => {
    if (!source) {
      setProbeState('unavailable');
      return;
    }
    let cancelled = false;
    setProbeState('probing');
    void (async () => {
      const outcome = await probeVideoSource(source, {
        retryMs: VIDEO_PROBE_RETRY_MS,
        maxAttempts: VIDEO_PROBE_MAX_ATTEMPTS,
        // The promise settles only on the TERMINAL verdict; without this the
        // transient 202 never reaches the UI and the slide reads
        // "Caricamento..." for the whole ladder-preparation wait instead of
        // switching to its dedicated "preparing" branch.
        onPhase: (phase) => {
          if (!cancelled && phase === 'preparing') setProbeState('preparing');
        },
        fetchImpl: ((
          uri: string,
          init: { headers: Record<string, string>; signal: AbortSignal },
        ) => fetch(uri, init as RequestInit)) as unknown as VideoProbeFetch,
      });
      if (cancelled) return;
      setProbeState(outcome.phase as ProbeState);
      setResolvedContainer(outcome.container ?? null);
    })();
    return () => {
      cancelled = true;
    };
  }, [source]);

  // Resolved expo-video source: exists ONLY on a confirmed ready, carrying
  // contentType:'hls' exactly when the server answered HLS.
  const [resolvedContainer, setResolvedContainer] = useState<VideoContainer | null>(null);

  const expoSource = useMemo(() => {
    if (probeState !== 'ready') return null;
    if (!source || resolvedContainer === null) return null;
    const base = { uri: source.uri, headers: source.headers };
    return resolvedContainer === 'hls'
      ? { ...base, contentType: 'hls' as const }
      : base;
  }, [source, probeState, resolvedContainer]);

  const player: VideoPlayer = useVideoPlayer(expoSource, (p) => {
    // Mount ≠ autoplay. Playback is owned exclusively by the `active` effect:
    // an unfocused neighbor must never start making noise.
    p.loop = false;
  });

  useEffect(() => {
    const statusSub = player.addListener('statusChange', (status) => {
      if (status.status === 'readyToPlay') setReady(true);
      if (status.status === 'error') {
        setError(
          status.error instanceof Error
            ? status.error.message
            : t('player.playbackError'),
        );
      }
    });

    const playingSub = player.addListener('playingChange', ({ isPlaying }) => {
      // Keep-awake belongs to the ACTIVE playing video only.
      if (isPlaying && activeRef.current) {
        void activateKeepAwakeAsync();
      } else {
        deactivateKeepAwake();
      }
    });

    return () => {
      statusSub.remove();
      playingSub.remove();
      deactivateKeepAwake();
      // Unmount always stops the audio, even during the teardown window.
      try {
        player.pause();
      } catch {
        /* already released */
      }
    };
  }, [player]);

  // Focus drives playback. Pausing on !active is what makes simultaneous
  // playback impossible while the pager keeps neighbors mounted.
  useEffect(() => {
    if (!active) {
      try {
        player.pause();
      } catch {
        /* released */
      }
      return;
    }
    if (expoSource !== null && ready && error === null) {
      void player.play();
    }
  }, [active, expoSource, ready, error, player]);

  const probing = probeState === 'probing';
  const preparing = probeState === 'preparing';

  if (source === null || probeState === 'unavailable') {
    // HLS off / item gone / no playable source: poster + explicit message.
    return (
      <View style={styles.centerDark}>
        {slide.posterUrl ? (
          <AuthedImage path={slide.posterUrl} style={styles.poster} accessibilityLabel="" />
        ) : null}
        <Text style={styles.errorText}>{t('grid.videoNoPoster')}</Text>
      </View>
    );
  }

  if (error !== null) {
    return (
      <View style={styles.centerDark}>
        <ErrorState title={t('player.playbackError')} message={error} />
      </View>
    );
  }

  return (
    <View style={[styles.full, styles.dark]}>
      {(!expoSource || probing || preparing) && (
        <View style={styles.loadingOverlay}>
          <LoadingState />
          <Text style={styles.loadingText}>
            {preparing ? t('player.preparing') : t('player.loading')}
          </Text>
        </View>
      )}
      <VideoView
        player={player}
        contentFit="contain"
        allowsFullscreen
        allowsPictureInPicture={false}
        style={styles.full}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  full: { width: '100%', height: '100%' },
  dark: { backgroundColor: '#0A0F1A' },
  centerDark: {
    flex: 1,
    backgroundColor: '#0A0F1A',
    alignItems: 'center',
    justifyContent: 'center',
  },
  loadingOverlay: {
    ...StyleSheet.absoluteFillObject,
    alignItems: 'center',
    justifyContent: 'center',
    backgroundColor: '#0A0F1A',
  },
  loadingText: { color: '#F5F7FB', marginTop: 12, fontSize: 14 },
  poster: { width: '86%', height: '52%', borderRadius: 12, marginBottom: 16 },
  errorText: { color: '#F5F7FB', padding: 24, textAlign: 'center' },
});

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

import React, { useEffect, useRef, useState } from 'react';
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
import type { ViewerSlide } from '../media/viewerSequence';

// Probe tuning: how long the slide waits for the HLS ladder to prepare
// before declaring the video unavailable.
const PROBE_RETRY_MS = 3000;
const PROBE_MAX_ATTEMPTS = 10; // ≈30 s of "Preparing" window

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

  // PREFLIGHT (acceptance): the shared /video route answers 202 while the HLS
  // ladder prepares, 404 when HLS is off or the item is gone, and 200 only
  // when real playback can start. The player mounts exclusively on a
  // confirmed 200 — expo-video never sees the intermediate states. The same
  // probe also covers the owned endpoint's 202-with-HLS behaviour.
  const [probe, setProbe] = useState<ProbeState>(
    slide.videoSource ? 'probing' : 'unavailable',
  );

  useEffect(() => {
    if (!source) return;
    let cancelled = false;
    let attempt = 0;
    void (async () => {
      while (!cancelled && attempt < PROBE_MAX_ATTEMPTS) {
        attempt += 1;
        try {
          const res = await fetch(source.uri, { headers: source.headers });
          if (cancelled) return;
          if (res.ok) {
            setProbe('ready');
            return;
          }
          if (res.status === 202) {
            setProbe('preparing');
            await new Promise((r) => setTimeout(r, PROBE_RETRY_MS));
            continue;
          }
          // 404 etc: HLS provider off, revoked membership, deleted item —
          // all deliberate non-availability, never worth retrying.
          setProbe('unavailable');
          return;
        } catch {
          // A network hiccup mid-probe behaves like "still preparing".
          setProbe('preparing');
          await new Promise((r) => setTimeout(r, PROBE_RETRY_MS));
        }
      }
      if (!cancelled) setProbe('unavailable'); // gave up waiting for the ladder
    })();
    return () => {
      cancelled = true;
    };
  }, [source]);

  const playerReady = source !== null && probe === 'ready';
  const player: VideoPlayer = useVideoPlayer(playerReady ? source : null, (p) => {
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
    if (playerReady && ready && error === null) {
      void player.play();
    }
  }, [active, playerReady, ready, error, player]);

  if (source === null || probe === 'unavailable') {
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
      {(!ready || probe === 'preparing' || probe === 'probing') && (
        <View style={styles.loadingOverlay}>
          <LoadingState />
          <Text style={styles.loadingText}>
            {probe === 'preparing' ? t('player.preparing') : t('player.loading')}
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

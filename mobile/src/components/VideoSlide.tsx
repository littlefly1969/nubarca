// VideoSlide: native expo-video playback over the authenticated Range endpoint.
// One player per slide instance (the pager only materializes the focused
// neighbor), released on unmount — audio never outlives the viewer.

import React, { useEffect, useMemo, useRef, useState } from 'react';
import { StyleSheet, Text, View } from 'react-native';
import {
  useVideoPlayer,
  VideoView,
  type VideoPlayer,
} from 'expo-video';
import { activateKeepAwakeAsync, deactivateKeepAwake } from 'expo-keep-awake';
import { buildVideoSource } from '../media/videoSource.ts';
import { ErrorState, LoadingState } from '../ui/states';
import { useI18n } from '../i18n';
import type { VideoMediaItem, MediaItem } from '../api/media';

export function VideoSlide({ item }: { item: MediaItem }): React.JSX.Element {
  const { t } = useI18n();
  const [error, setError] = useState<string | null>(null);
  const [ready, setReady] = useState(false);
  const playingRef = useRef(false);
  // The pager only mounts video slides for kind:'video' items. The source is
  // memoized on the item id so a re-render (ready/error state changes) can
  // NEVER hand useVideoPlayer a fresh object identity — expo-video would
  // otherwise recreate the player and restart playback mid-stream.
  const source = useMemo(() => buildVideoSource(item as VideoMediaItem), [item]);

  const player: VideoPlayer = useVideoPlayer(source?.source ?? null, (p) => {
    p.loop = false;
    if (source !== null) void p.play();
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
      playingRef.current = isPlaying;
      if (isPlaying) {
        void activateKeepAwakeAsync();
      } else {
        deactivateKeepAwake();
      }
    });
    return () => {
      statusSub.remove();
      playingSub.remove();
      deactivateKeepAwake();
      // Releasing happens with the player on unmount; pause first so audio
      // stops even during the teardown window.
      try {
        player.pause();
      } catch {
        /* already released */
      }
    };
  }, [player]);

  if (source === null) {
    return (
      <View style={styles.centerDark}>
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
      {!ready && (
        <View style={styles.loadingOverlay}>
          <LoadingState />
          <Text style={styles.loadingText}>{t('player.loading')}</Text>
        </View>
      )}
      <VideoView
        player={player}
        contentFit="contain"
        allowsFullscreen
        allowsPictureInPicture={false}
        requiresLinearPlayback={false}
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
  errorText: { color: '#F5F7FB', padding: 24, textAlign: 'center' },
});

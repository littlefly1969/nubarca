// /media/[id]: full-screen media viewer.
//
// Images: dark surface, swipe through the loaded sequence (virtualized,
// bounded neighbor window), pinch/double-tap zoom with pan, single tap toggles
// chrome. Medium previews only — never originals.
//
// Videos: expo-video over the authenticated Range endpoint (videoSource builds
// the Cookie header). Resources release on unmount.
import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  BackHandler,
  Pressable,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
} from 'react-native';
import { Redirect, router, useFocusEffect, useLocalSearchParams } from 'expo-router';
import { FlatList } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { ImageSlide } from '../../src/components/ImageSlide';
import { VideoSlide } from '../../src/components/VideoSlide';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import type { MediaItem, VideoMediaItem, ImageMediaItem } from '../../src/api/media.ts';
import { colors, spacing } from '../../src/ui/tokens';
import { useI18n } from '../../src/i18n';

export default function MediaRoute(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const params = useLocalSearchParams<{ id: string; kind?: string; name?: string }>();
  const { sequence } = useViewer();
  const { width } = useWindowDimensions();

  // Sequence comes from the grid context. A files-mode entry (no context)
  // degrades to a single-item sequence built from route params.
  const items: MediaItem[] = useMemo(() => {
    if (sequence !== null && sequence.items.some((i) => i.id === params.id)) {
      return sequence.items;
    }
    const isVideo = params.kind === 'video';
    const base = {
      id: params.id,
      name: params.name ?? '',
      title: null,
      displayName: params.name ?? '',
      mimeType: isVideo ? 'video/mp4' : 'image/*',
      sizeBytes: 0,
      width: null,
      height: null,
      createdAt: '',
      updatedAt: null,
      takenAt: null,
      thumbnailUrl:
        params.kind === 'video'
          ? `/api/files/${params.id}/poster`
          : `/api/files/${params.id}/thumbnail?size=small`,
      occurrenceCount: 1,
      hasDuplicates: false,
    };
    const fallback: MediaItem = isVideo
      ? ({
          ...base,
          kind: 'video',
          posterUrl: base.thumbnailUrl,
          durationSeconds: null,
          videoCodec: null,
          audioCodec: null,
          hasAudio: null,
          frameRate: null,
          posterSource: null,
          previewStripUrl: null,
        } satisfies VideoMediaItem)
      : ({ ...base, kind: 'image' } satisfies ImageMediaItem);
    return [fallback];
  }, [sequence, params.id, params.kind, params.name]);

  const startIndex = Math.max(
    0,
    items.findIndex((i) => i.id === params.id),
  );
  const [index, setIndex] = useState(startIndex);
  const [chromeVisible, setChromeVisible] = useState(true);

  useEffect(() => {
    setIndex(startIndex);
  }, [startIndex]);

  const current = items[index];

  const onMomentumEnd = useCallback(
    (e: { nativeEvent: { contentOffset: { x: number } } }) => {
      const next = Math.round(e.nativeEvent.contentOffset.x / width);
      if (next !== index && next >= 0 && next < items.length) setIndex(next);
    },
    [index, items.length, width],
  );

  // Hardware back pops the viewer; the zoom gesture never traps it because
  // the responder never captures a single unzoomed touch.
  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => {
      if (router.canGoBack()) router.back();
      return true;
    });
    return () => sub.remove();
  }, []);

  // Leaving the screen (back/gesture) is enough — VideoSlide cleanup pauses
  // playback and releases the player on unmount.
  useFocusEffect(
    useCallback(() => undefined, []),
  );

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  return (
    <View style={styles.root}>
      <FlatList
        data={items}
        horizontal
        pagingEnabled
        keyExtractor={(i) => i.id}
        initialScrollIndex={startIndex}
        getItemLayout={(_data, i) => ({
          length: width,
          offset: width * i,
          index: i,
        })}
        initialNumToRender={1}
        maxToRenderPerBatch={2}
        windowSize={3}
        onMomentumScrollEnd={onMomentumEnd}
        renderItem={({ item }) =>
          item.kind === 'image' ? (
            <ImageSlide
              path={`/api/files/${item.id}/preview`}
              name={item.displayName}
              onToggle={() => setChromeVisible((v) => !v)}
            />
          ) : (
            <VideoSlide item={item} />
          )
        }
        style={{ width }}
      />

      {chromeVisible && (
        <View style={styles.chromeTop} pointerEvents="box-none">
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('viewer.back')}
            onPress={() => router.back()}
            style={({ pressed }) => [styles.backBtn, pressed && styles.pressed]}
            hitSlop={8}
          >
            <Ionicons name="arrow-back" size={24} color="#fff" />
          </Pressable>
          <Text style={styles.title} numberOfLines={1} ellipsizeMode="middle">
            {current.displayName}
          </Text>
          <Text style={styles.counter}>{`${index + 1} / ${items.length}`}</Text>
        </View>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  root: {
    flex: 1,
    backgroundColor: colors.mediaBackground,
  },
  chromeTop: {
    position: 'absolute',
    top: 0,
    left: 0,
    right: 0,
    flexDirection: 'row',
    alignItems: 'center',
    gap: spacing.m,
    paddingHorizontal: spacing.m,
    paddingTop: spacing.xl + spacing.l,
    paddingBottom: spacing.s,
    backgroundColor: 'rgba(10,15,26,0.45)',
  },
  backBtn: {
    width: 44,
    height: 44,
    borderRadius: 22,
    backgroundColor: 'rgba(255,255,255,0.12)',
    alignItems: 'center',
    justifyContent: 'center',
  },
  title: {
    flex: 1,
    color: '#FFFFFF',
    fontSize: 14,
    fontWeight: '600',
  },
  counter: {
    color: 'rgba(255,255,255,0.75)',
    fontSize: 12,
    fontVariant: ['tabular-nums'],
  },
  pressed: { opacity: 0.7 },
});

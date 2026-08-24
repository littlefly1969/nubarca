// /media/[id]: full-screen media viewer over PRE-BUILT viewer slides.
//
// The opening screen hands the whole sequence to the viewer context with every
// media URL already resolved (owned builders or server-provided shared URLs).
// This route never rebuilds a path and never learns whether the album was
// owned or shared.
//
// Videos play only when their slide is the focused one (active flag).

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  BackHandler,
  Pressable,
  StyleSheet,
  Text,
  View,
  useWindowDimensions,
} from 'react-native';
import { Redirect, router, useLocalSearchParams } from 'expo-router';
import { FlatList } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { ImageSlide } from '../../src/components/ImageSlide';
import { VideoSlide } from '../../src/components/VideoSlide';
import { useSession } from '../../src/session/SessionProvider';
import { useViewer } from '../../src/media/viewerContext';
import type { ViewerSlide } from '../../src/media/viewerSequence';
import {
  filePosterPath,
  filePreviewPath,
  fileVideoPath,
} from '../../src/api/filePaths';
import { authenticatedSource } from '../../src/media/imageSource';
import { colors, spacing } from '../../src/ui/tokens';
import { useI18n } from '../../src/i18n';

export default function MediaRoute(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const params = useLocalSearchParams<{ id: string; kind?: string; name?: string }>();
  const { sequence, setIndex: setViewerIndex, close: closeViewer } = useViewer();
  const { width } = useWindowDimensions();

  // ONE exit path for both the hardware back and the chrome button: release
  // the sequence, then leave — with a SAFE fallback when this route was
  // entered without usable history (deep link / state restore), where a bare
  // router.back() would swallow the Android back key forever.
  const closeAndLeave = useCallback(() => {
    closeViewer();
    if (router.canGoBack()) router.back();
    else router.replace('/(tabs)/photos');
  }, [closeViewer]);

  // Slides arrive pre-built through the viewer context. A files-mode entry
  // (no sequence) degrades to ONE OWNED slide built from route params —
  // folder browsing is owner-only in this slice, so owner paths are correct
  // here by construction.
  const slides: ViewerSlide[] = useMemo(() => {
    if (sequence !== null && sequence.slides.some((s) => s.key === params.id)) {
      return sequence.slides;
    }
    if (params.kind === 'video') {
      const src = authenticatedSource(fileVideoPath(params.id));
      return [
        {
          key: params.id,
          kind: 'video',
          displayName: params.name ?? '',
          imagePath: '',
          videoSource: src ? { uri: src.uri, headers: src.headers } : null,
          posterUrl: filePosterPath(params.id),
        },
      ];
    }
    return [
      {
        key: params.id,
        kind: 'image',
        displayName: params.name ?? '',
        imagePath: filePreviewPath(params.id),
        videoSource: null,
        posterUrl: null,
      },
    ];
  }, [sequence, params.id, params.kind, params.name]);

  const startIndex = Math.max(
    0,
    slides.findIndex((s) => s.key === params.id),
  );
  const [index, setIndex] = useState(startIndex);
  const [chromeVisible, setChromeVisible] = useState(true);

  useEffect(() => {
    setIndex(startIndex);
  }, [startIndex]);

  const current = slides[index];

  const onMomentumEnd = useCallback(
    (e: { nativeEvent: { contentOffset: { x: number } } }) => {
      const next = Math.round(e.nativeEvent.contentOffset.x / width);
      if (next !== index && next >= 0 && next < slides.length) {
        setIndex(next);
        setViewerIndex(next);
      }
    },
    [index, slides.length, width, setViewerIndex],
  );

  // Hardware back uses the SAME safe exit path as the chrome button.
  useEffect(() => {
    const sub = BackHandler.addEventListener('hardwareBackPress', () => {
      closeAndLeave();
      return true;
    });
    return () => sub.remove();
  }, [closeAndLeave]);

  if (session.status !== 'authed') {
    return <Redirect href="/login" />;
  }

  return (
    <View style={styles.root}>
      <FlatList
        data={slides}
        horizontal
        pagingEnabled
        keyExtractor={(s) => s.key}
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
        renderItem={({ item, index: i }) =>
          item.kind === 'image' ? (
            <ImageSlide
              path={item.imagePath}
              name={item.displayName}
              onToggle={() => setChromeVisible((v) => !v)}
            />
          ) : (
            <VideoSlide slide={item} active={i === index} />
          )
        }
        style={{ width }}
      />

      {chromeVisible && (
        <View style={styles.chromeTop} pointerEvents="box-none">
          <Pressable
            accessibilityRole="button"
            accessibilityLabel={t('viewer.back')}
            onPress={closeAndLeave}
            style={({ pressed }) => [styles.backBtn, pressed && styles.pressed]}
            hitSlop={8}
          >
            <Ionicons name="arrow-back" size={24} color="#fff" />
          </Pressable>
          <Text style={styles.title} numberOfLines={1} ellipsizeMode="middle">
            {current.displayName}
          </Text>
          <Text style={styles.counter}>{`${index + 1} / ${slides.length}`}</Text>
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

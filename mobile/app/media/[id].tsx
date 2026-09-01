// /media/[id]: full-screen media viewer over PRE-BUILT viewer slides.
//
// The opening screen hands the whole sequence to the viewer context with every
// media URL already resolved (owned builders or server-provided shared URLs).
// This route never rebuilds a path and never learns whether the album was
// owned or shared.
//
// Videos play only when their slide is the focused one (active flag).

import React, {
  useCallback,
  useEffect,
  useLayoutEffect,
  useMemo,
  useRef,
  useState,
} from 'react';
import {
  BackHandler,
  type LayoutChangeEvent,
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
import { forgetAllPositions } from '../../src/media/videoPosition';
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
import {
  safeViewerIndex,
  shouldReanchorViewer,
  viewerContentCanReachIndex,
  viewerIndexFromUserScroll,
  viewerOffsetForIndex,
} from '../../src/media/viewerRoute';
import { colors, spacing } from '../../src/ui/tokens';
import { useI18n } from '../../src/i18n';

export default function MediaRoute(): React.JSX.Element {
  const session = useSession();
  const { t } = useI18n();
  const params = useLocalSearchParams<{ id: string; kind?: string; name?: string }>();
  const { sequence, setIndex: setViewerIndex, close: closeViewer } = useViewer();
  const { width: initialWindowWidth } = useWindowDimensions();

  // ONE navigation path for both the hardware back and the chrome button. The
  // route owns cleanup on unmount, so no render can observe a sequence erased
  // while this screen is still mounted.
  const closeAndLeave = useCallback(() => {
    if (router.canGoBack()) router.back();
    else router.replace('/(tabs)/photos');
  }, []);

  useEffect(() => {
    return () => {
      closeViewer();
      // Leaving the viewer ends the reason to remember where a video was. The
      // store is module-owned so it can outlive a slide remount; it must not
      // outlive the viewer itself.
      forgetAllPositions();
    };
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
  // GESTURE OWNERSHIP (§4). The active photo slide reports whether it is at
  // rest; while it is zoomed the pager stops scrolling so a pan cannot page to
  // the neighbouring item, and the instant zoom returns to 1 paging is live
  // again. Derived from zoom state, never from a timeout.
  const [pagerOwnsHorizontal, setPagerOwnsHorizontal] = useState(true);
  // Window dimensions change before Android has necessarily laid the native
  // list out. The pager's own onLayout measurement is the only width allowed
  // to drive cell sizes, offsets and swipe interpretation.
  const [pagerWidth, setPagerWidth] = useState(initialWindowWidth);
  const pagerRef = useRef<FlatList<ViewerSlide>>(null);
  const previousWidthRef = useRef(pagerWidth);
  const activeDragWidthRef = useRef<number | null>(null);
  const pendingReanchorRef = useRef<{ index: number; width: number } | null>(null);

  useEffect(() => {
    setIndex(startIndex);
  }, [startIndex]);

  // A new item is always at rest: the outgoing slide's zoom must never leave
  // the pager locked on the incoming one.
  useEffect(() => {
    setPagerOwnsHorizontal(true);
  }, [index]);

  const safeIndex = safeViewerIndex(index, slides.length);
  const current = slides[safeIndex];

  useLayoutEffect(() => {
    const previousWidth = previousWidthRef.current;
    previousWidthRef.current = pagerWidth;
    if (!shouldReanchorViewer(previousWidth, pagerWidth)) return;

    // A gesture that began in the old geometry cannot finish into the new one.
    activeDragWidthRef.current = null;
    const pager = pagerRef.current;
    if (pager === null || slides.length === 0) return;
    pendingReanchorRef.current = { index: safeIndex, width: pagerWidth };
    // Direct pixels deliberately bypass FlatList's cached pre-rotation item
    // frames. The offset and cells are both based on this measured width.
    pager.scrollToOffset({
      offset: viewerOffsetForIndex(safeIndex, pagerWidth, slides.length),
      animated: false,
    });
  }, [pagerWidth, safeIndex, slides.length]);

  const onPagerLayout = useCallback((event: LayoutChangeEvent) => {
    const measuredWidth = event.nativeEvent.layout.width;
    if (Number.isFinite(measuredWidth) && measuredWidth > 0) {
      setPagerWidth((current) =>
        current === measuredWidth ? current : measuredWidth,
      );
    }
  }, []);

  const onPagerContentSizeChange = useCallback(
    (contentWidth: number) => {
      const pending = pendingReanchorRef.current;
      if (pending === null || pending.width !== pagerWidth) return;
      if (
        !viewerContentCanReachIndex(
          contentWidth,
          pending.index,
          pending.width,
          slides.length,
        )
      ) {
        return;
      }
      const pager = pagerRef.current;
      if (pager === null) return;
      pager.scrollToOffset({
        offset: viewerOffsetForIndex(
          pending.index,
          pending.width,
          slides.length,
        ),
        animated: false,
      });
      pendingReanchorRef.current = null;
    },
    [pagerWidth, slides.length],
  );

  const onScrollBeginDrag = useCallback(() => {
    pendingReanchorRef.current = null;
    activeDragWidthRef.current = pagerWidth;
  }, [pagerWidth]);

  const onMomentumEnd = useCallback(
    (e: { nativeEvent: { contentOffset: { x: number } } }) => {
      const dragWidth = activeDragWidthRef.current;
      activeDragWidthRef.current = null;
      const next = viewerIndexFromUserScroll(
        e.nativeEvent.contentOffset.x,
        dragWidth,
        pagerWidth,
        slides.length,
      );
      if (next === null) return;
      if (next !== safeIndex && next >= 0 && next < slides.length) {
        setIndex(next);
        setViewerIndex(next);
      }
    },
    [pagerWidth, safeIndex, slides.length, setViewerIndex],
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
        ref={pagerRef}
        onLayout={onPagerLayout}
        onContentSizeChange={onPagerContentSizeChange}
        data={slides}
        horizontal
        pagingEnabled
        scrollEnabled={pagerOwnsHorizontal}
        keyExtractor={(s) => s.key}
        initialScrollIndex={startIndex}
        getItemLayout={(_data, i) => ({
          length: pagerWidth,
          offset: pagerWidth * i,
          index: i,
        })}
        initialNumToRender={1}
        maxToRenderPerBatch={2}
        windowSize={3}
        onMomentumScrollEnd={onMomentumEnd}
        onScrollBeginDrag={onScrollBeginDrag}
        renderItem={({ item, index: i }) => (
          <View style={{ width: pagerWidth, flex: 1 }}>
            {item.kind === 'image' ? (
              <ImageSlide
                path={item.imagePath}
                name={item.displayName}
                active={i === safeIndex}
                onToggle={() => setChromeVisible((v) => !v)}
                onZoomOwnershipChange={
                  i === safeIndex ? setPagerOwnsHorizontal : undefined
                }
              />
            ) : (
              <VideoSlide slide={item} active={i === safeIndex} />
            )}
          </View>
        )}
        style={styles.pager}
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
          {current !== undefined && (
            <>
              <Text style={styles.title} numberOfLines={1} ellipsizeMode="middle">
                {current.displayName}
              </Text>
              <Text style={styles.counter}>{`${safeIndex + 1} / ${slides.length}`}</Text>
            </>
          )}
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
  pager: {
    flex: 1,
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

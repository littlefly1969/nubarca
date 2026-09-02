// The immersive gallery shell (NUBARCA-UX-01 §2, §4, §13).
//
// A gallery owns the screen. The content scrolls edge to edge and the chrome
// floats over it: a top overlay that gets out of the way as you travel into the
// library and comes back the moment you reach for it, and the bottom navigation
// which stays put.
//
// WHAT THE SHELL DOES NOT OWN, and this is the whole design: no data, no
// pagination, no filter model, no selection capabilities, no navigation state.
// It positions chrome and reports how much room that chrome takes. A screen
// hands it a title, some actions and a list; the screen keeps its domain.
//
// SCROLLING MUST NOT RE-RENDER REACT. The decision is a pure rule
// (galleryChrome.ts) evaluated in a ref, and the only thing that moves is an
// Animated value driven by `setValue`. A scroll handler that called setState
// would re-render the gallery on every frame of every flick.

import React, { useCallback, useMemo, useRef, useState } from 'react';
import {
  AccessibilityInfo,
  Animated,
  Easing,
  StyleSheet,
  View,
  type LayoutChangeEvent,
  type NativeScrollEvent,
  type NativeSyntheticEvent,
} from 'react-native';
import { useSafeAreaInsets } from 'react-native-safe-area-context';
import { motion, spacing } from './tokens';
import { themed } from './theme';
import {
  initialGalleryChromeState,
  nextGalleryChromeState,
  type GalleryChromeState,
} from './galleryChrome.ts';

/** What a gallery needs in order to sit correctly inside the shell. */
export interface GalleryScrollProps {
  onScroll: (event: NativeSyntheticEvent<NativeScrollEvent>) => void;
  scrollEventThrottle: number;
  /** Room for the top chrome, so the first row starts below it at rest. */
  contentPaddingTop: number;
  /** Room for the bottom navigation, so the last row clears it. */
  contentPaddingBottom: number;
}

export function ImmersiveGalleryShell({
  topChrome,
  bottomOverlayHeight = 0,
  children,
}: {
  /** Title, actions and filter chips. Travels with the collapsible overlay. */
  topChrome: React.ReactNode;
  /** Footprint of whatever floats at the bottom, excluding the safe area. */
  bottomOverlayHeight?: number;
  children: (scroll: GalleryScrollProps) => React.ReactNode;
}): React.JSX.Element {
  const styles = useStyles();
  const insets = useSafeAreaInsets();
  const [chromeHeight, setChromeHeight] = useState(0);
  const [reduceMotion, setReduceMotion] = useState(false);

  React.useEffect(() => {
    let cancelled = false;
    void AccessibilityInfo.isReduceMotionEnabled().then(
      (on) => {
        if (!cancelled) setReduceMotion(on);
      },
      () => {},
    );
    const sub = AccessibilityInfo.addEventListener('reduceMotionChanged', setReduceMotion);
    return () => {
      cancelled = true;
      sub.remove();
    };
  }, []);

  const translate = useRef(new Animated.Value(0)).current;
  const state = useRef<GalleryChromeState>(initialGalleryChromeState);
  const hidden = useRef(false);

  const onScroll = useCallback(
    (event: NativeSyntheticEvent<NativeScrollEvent>) => {
      const y = event.nativeEvent.contentOffset.y;
      const next = nextGalleryChromeState(state.current, y);
      state.current = next;
      if (next.hidden === hidden.current) return;
      hidden.current = next.hidden;
      Animated.timing(translate, {
        toValue: next.hidden ? -(chromeHeight || 1) : 0,
        // Reduced motion snaps rather than travels: the chrome still gets out
        // of the way, it just does not slide there.
        duration: reduceMotion ? 0 : motion.durationMs.standard,
        easing: Easing.bezier(...motion.easing.bezier),
        useNativeDriver: true,
      }).start();
    },
    [translate, chromeHeight, reduceMotion],
  );

  const onChromeLayout = useCallback((event: LayoutChangeEvent) => {
    setChromeHeight(event.nativeEvent.layout.height);
  }, []);

  const scroll = useMemo<GalleryScrollProps>(
    () => ({
      onScroll,
      scrollEventThrottle: 16,
      contentPaddingTop: chromeHeight,
      // The last row must clear the bottom overlay: padding INSIDE the scroll
      // content, never an opaque strip carved out beside it.
      contentPaddingBottom: bottomOverlayHeight + insets.bottom + spacing.l,
    }),
    [onScroll, chromeHeight, bottomOverlayHeight, insets.bottom],
  );

  return (
    <View style={styles.root}>
      {children(scroll)}
      <Animated.View
        style={[
          styles.topChrome,
          { paddingTop: insets.top, transform: [{ translateY: translate }] },
        ]}
        onLayout={onChromeLayout}
        pointerEvents="box-none"
      >
        {topChrome}
      </Animated.View>
    </View>
  );
}

const useStyles = themed((colors) =>
  StyleSheet.create({
    root: { flex: 1, backgroundColor: colors.canvas },
    // Floats OVER the gallery. The safe-area inset lives inside it, so hiding
    // the chrome takes the status-bar padding with it and the media really does
    // reach the top of the screen.
    topChrome: {
      position: 'absolute',
      top: 0,
      left: 0,
      right: 0,
      backgroundColor: colors.canvas,
    },
  }),
);

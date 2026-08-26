// ImageSlide: one full-screen authenticated image with zoom gestures.
//
// Gesture wiring: pinch (2 touches) scales via the pure transform; a single
// finger pans only while zoomed so taps still toggle chrome; release under
// ~1x snaps back. Double-tap toggles 1x↔2x.

import React, { useCallback, useMemo, useRef, useState } from 'react';
import { Pressable, StyleSheet, View } from 'react-native';
import { AuthedImage } from './AuthedImage';
import {
  applyPan,
  applyPinch,
  identity,
  release,
  toggleZoom,
  type ZoomState,
} from '../media/zoomTransform';

interface TouchLike {
  pageX: number;
  pageY: number;
}

export function ImageSlide({
  path,
  name,
  onToggle,
}: {
  path: string;
  name: string;
  onToggle: () => void;
}): React.JSX.Element {
  const [zoom, setZoom] = useState<ZoomState>(identity());
  const stateRef = useRef<ZoomState>(identity());
  const lastTap = useRef(0);
  const twoFingerStartDist = useRef<number | null>(null);
  const panStart = useRef<{ x: number; y: number } | null>(null);
  const activeTouches = useRef<TouchLike[]>([]);

  const commit = useCallback((next: ZoomState) => {
    stateRef.current = next;
    setZoom(next);
  }, []);

  const { PanResponder } = require('react-native') as typeof import('react-native');
  const panResponder = useMemo(() => {
    return PanResponder.create({
      onStartShouldSetPanResponder: () => false,
      onStartShouldSetPanResponderCapture: () => false,
      onMoveShouldSetPanResponder: (
        _e: import('react-native').GestureResponderEvent,
        gs: import('react-native').PanResponderGestureState,
      ) => gs.numberActiveTouches >= 2 || stateRef.current.scale > 1.05,
      onPanResponderGrant: () => {
        twoFingerStartDist.current = null;
        panStart.current = null;
        activeTouches.current = [];
      },
      onPanResponderMove: (evt: import('react-native').GestureResponderEvent) => {
        const touches: TouchLike[] = evt.nativeEvent.touches;
        if (touches.length >= 2) {
          const dist = Math.hypot(
            touches[0].pageX - touches[1].pageX,
            touches[0].pageY - touches[1].pageY,
          );
          if (twoFingerStartDist.current === null) {
            twoFingerStartDist.current = dist;
          } else if (twoFingerStartDist.current > 4 && dist > 0) {
            commit(applyPinch(stateRef.current, dist / twoFingerStartDist.current));
            twoFingerStartDist.current = dist; // continuous pinch
          }
        } else if (stateRef.current.scale > 1.05) {
          const t = touches[0];
          if (t !== undefined) {
            if (panStart.current === null) panStart.current = { x: t.pageX, y: t.pageY };
            commit(
              applyPan(stateRef.current, t.pageX - panStart.current.x, t.pageY - panStart.current.y),
            );
          }
        }
      },
      onPanResponderRelease: () => {
        commit(release(stateRef.current));
        twoFingerStartDist.current = null;
        panStart.current = null;
      },
      onPanResponderTerminate: () => {
        commit(release(stateRef.current));
        twoFingerStartDist.current = null;
        panStart.current = null;
      },
    });
  }, [commit]);

  function onTap(): void {
    const now = Date.now();
    if (now - lastTap.current < 280) {
      commit(toggleZoom(stateRef.current));
      lastTap.current = 0;
      return;
    }
    lastTap.current = now;
    onToggle();
  }

  return (
    <Pressable style={[styles.slide, styles.full]} onPress={onTap}>
      <View {...panResponder.panHandlers} style={[styles.zoomArea, styles.full]}>
        <AuthedImage
          path={path}
          style={{
            ...styles.full,
            transform: [
              { translateX: zoom.tx },
              { translateY: zoom.ty },
              { scale: zoom.scale },
            ],
          }}
          accessibilityLabel={name}
        />
      </View>
    </Pressable>
  );
}

const styles = StyleSheet.create({
  full: { width: '100%', height: '100%' },
  slide: { flex: 1, backgroundColor: '#0A0F1A' },
  zoomArea: { alignItems: 'center', justifyContent: 'center' },
});

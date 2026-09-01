// ImageSlide: one full-screen authenticated photo with native pinch/pan/
// double-tap (MOBILE-FIRST-CLASS-PARITY-01 §3-6).
//
// GESTURES come from react-native-gesture-handler and run on the UI thread
// through Reanimated, so a drag stays smooth while JS is busy rendering
// neighbouring slides. The hand-rolled PanResponder engine this replaces could
// not do that, and §6 asks for the standard primitives rather than a bespoke
// multi-touch engine.
//
// ALL THE MATH lives in media/zoomTransform.ts, which is pure and unit-tested.
// This file only wires gestures to it and reports ownership upward. In
// particular it invents no bounds of its own: an image can never be dragged
// off-screen and left there, because every pan goes through the clamp.
//
// GESTURE OWNERSHIP (§4) is derived from state, never from a timeout:
//   scale == 1  -> the horizontal gesture belongs to the PAGER
//   scale  > 1  -> the pan belongs to the IMAGE
// The slide reports which is true through `onZoomOwnershipChange`, and the
// viewer turns that straight into the pager's `scrollEnabled`. The moment zoom
// returns to 1, paging works again — there is nothing to wait for.
//
// LIFECYCLE (§5): zoom is local to this slide. It resets when the slide stops
// being the active one, and when the measured geometry changes (rotation), so
// no scale or offset can leak into the next photo or survive a layout that no
// longer exists. What must NOT change on rotation is which item is selected —
// that is the viewer's business, and this component never touches it.

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { StyleSheet, View, type LayoutChangeEvent } from 'react-native';
import { Gesture, GestureDetector } from 'react-native-gesture-handler';
import Animated, {
  runOnJS,
  useAnimatedStyle,
  useSharedValue,
  withTiming,
} from 'react-native-reanimated';
import { AuthedImage } from './AuthedImage';
import {
  DOUBLE_TAP_SCALE,
  MAX_SCALE,
  MIN_SCALE,
  fittedSize,
  geometryChanged,
  maxTranslation,
  type Size,
} from '../media/zoomTransform';
import { media } from '../ui/palette.ts';

const NO_SIZE: Size = { width: 0, height: 0 };

export function ImageSlide({
  path,
  name,
  onToggle,
  active = true,
  onZoomOwnershipChange,
}: {
  path: string;
  name: string;
  onToggle: () => void;
  // The pager keeps neighbours mounted; only the focused slide may hold zoom.
  active?: boolean;
  // True while the PAGER should own horizontal drags (i.e. not zoomed).
  onZoomOwnershipChange?: (pagerOwnsHorizontal: boolean) => void;
}): React.JSX.Element {
  const scale = useSharedValue(MIN_SCALE);
  const tx = useSharedValue(0);
  const ty = useSharedValue(0);
  // Gesture-start anchors, so a pinch/pan is relative to where it began.
  const startScale = useSharedValue(MIN_SCALE);
  const startX = useSharedValue(0);
  const startY = useSharedValue(0);

  // Mirrors the zoom on the JS thread. It exists ONLY to enable/disable the
  // pan gesture: a gesture-handler Pan that is merely inert still CLAIMS the
  // touch, so the pager never sees a horizontal drag. Bailing out inside
  // onUpdate looks like it disables panning and does not — that is exactly the
  // bug this replaces, and photos could not be swiped at all.
  const [zoomed, setZoomed] = useState(false);
  const [viewport, setViewport] = useState<Size>(NO_SIZE);
  const [source, setSource] = useState<Size>(NO_SIZE);
  const previousViewport = useRef<Size>(NO_SIZE);

  const fitted = fittedSize(viewport, source);
  // Shared copies so the UI-thread clamp does not have to read React state.
  const limitBase = useSharedValue({ vw: 0, vh: 0, fw: 0, fh: 0 });
  limitBase.value = {
    vw: viewport.width, vh: viewport.height, fw: fitted.width, fh: fitted.height,
  };

  const reportOwnership = useCallback(
    (pagerOwns: boolean) => {
      // One place decides both: what the pager may do, and whether the pan
      // gesture is armed. They can never disagree.
      setZoomed(!pagerOwns);
      onZoomOwnershipChange?.(pagerOwns);
    },
    [onZoomOwnershipChange],
  );

  const reset = useCallback(
    (animated: boolean) => {
      if (animated) {
        scale.value = withTiming(MIN_SCALE);
        tx.value = withTiming(0);
        ty.value = withTiming(0);
      } else {
        scale.value = MIN_SCALE;
        tx.value = 0;
        ty.value = 0;
      }
      reportOwnership(true);
    },
    [reportOwnership, scale, tx, ty],
  );

  // Leaving focus, or a real geometry change (rotation), returns this slide to
  // rest. Both are cheap and both prevent stale offsets from a layout or a
  // photo that is no longer on screen.
  useEffect(() => {
    if (!active) reset(false);
  }, [active, reset]);

  useEffect(() => {
    if (geometryChanged(previousViewport.current, viewport)) reset(false);
    if (viewport.width > 0) previousViewport.current = viewport;
  }, [viewport, reset]);

  const onLayout = useCallback((event: LayoutChangeEvent) => {
    const { width, height } = event.nativeEvent.layout;
    setViewport((previous) =>
      previous.width === width && previous.height === height
        ? previous
        : { width, height });
  }, []);

  // The clamp, duplicated onto the UI thread as a worklet. It is the same rule
  // as maxTranslation(): half the overflow of the fitted box at this scale.
  const clampToBounds = useCallback(() => {
    'worklet';
    const { vw, vh, fw, fh } = limitBase.value;
    const limX = Math.max(0, (fw * scale.value - vw) / 2);
    const limY = Math.max(0, (fh * scale.value - vh) / 2);
    tx.value = Math.min(limX, Math.max(-limX, tx.value)) + 0;
    ty.value = Math.min(limY, Math.max(-limY, ty.value)) + 0;
  }, [limitBase, scale, tx, ty]);

  const pinch = Gesture.Pinch()
    .onStart(() => {
      startScale.value = scale.value;
    })
    .onUpdate((e) => {
      const next = Math.min(MAX_SCALE, Math.max(MIN_SCALE, startScale.value * e.scale));
      const crossed = (next > 1.05) !== (scale.value > 1.05);
      scale.value = next;
      clampToBounds();
      // Arm the pan the moment the pinch crosses the threshold, so the user
      // can drag without lifting their fingers first.
      if (crossed) runOnJS(reportOwnership)(next <= 1.05);
    })
    .onEnd(() => {
      if (scale.value <= 1.05) {
        scale.value = withTiming(MIN_SCALE);
        tx.value = withTiming(0);
        ty.value = withTiming(0);
        runOnJS(reportOwnership)(true);
      } else {
        runOnJS(reportOwnership)(false);
      }
    });

  // ENABLED, not just inert (§4). At rest the pan gesture must not exist as
  // far as the touch system is concerned, so the horizontal drag reaches the
  // pager and paging works the instant zoom returns to 1.
  const pan = Gesture.Pan()
    .enabled(zoomed)
    .onStart(() => {
      startX.value = tx.value;
      startY.value = ty.value;
    })
    .onUpdate((e) => {
      tx.value = startX.value + e.translationX;
      ty.value = startY.value + e.translationY;
      clampToBounds();
    });

  const doubleTap = Gesture.Tap()
    .numberOfTaps(2)
    .onEnd(() => {
      if (scale.value > 1.05) {
        scale.value = withTiming(MIN_SCALE);
        tx.value = withTiming(0);
        ty.value = withTiming(0);
        runOnJS(reportOwnership)(true);
      } else {
        scale.value = withTiming(DOUBLE_TAP_SCALE);
        tx.value = withTiming(0);
        ty.value = withTiming(0);
        runOnJS(reportOwnership)(false);
      }
    });

  // A single tap toggles the chrome, but only once the double-tap has been
  // ruled out — that is gesture-handler's own arbitration, not a timer of ours.
  const singleTap = Gesture.Tap()
    .numberOfTaps(1)
    .requireExternalGestureToFail(doubleTap)
    .onEnd(() => {
      runOnJS(onToggle)();
    });

  const gesture = Gesture.Simultaneous(
    pinch,
    pan,
    Gesture.Exclusive(doubleTap, singleTap),
  );

  const animatedStyle = useAnimatedStyle(() => ({
    transform: [
      { translateX: tx.value },
      { translateY: ty.value },
      { scale: scale.value },
    ],
  }));

  return (
    <View style={[styles.slide, styles.full]} onLayout={onLayout}>
      <GestureDetector gesture={gesture}>
        <Animated.View style={[styles.zoomArea, styles.full, animatedStyle]}>
          <AuthedImage
            path={path}
            resizeMode="contain"
            style={styles.full}
            accessibilityLabel={name}
            onNaturalSize={setSource}
          />
        </Animated.View>
      </GestureDetector>
    </View>
  );
}

// Exported for the viewer's own bounds assertions and for tests that need the
// same geometry the component uses.
export { fittedSize, maxTranslation };

const styles = StyleSheet.create({
  full: { width: '100%', height: '100%' },
  slide: { flex: 1, backgroundColor: media.background },
  zoomArea: { alignItems: 'center', justifyContent: 'center' },
});

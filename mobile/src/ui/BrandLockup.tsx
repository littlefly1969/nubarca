// The NubArca wordmark, on either surface (BRAND-LOGO-01).
//
// Two things this component exists to prevent.
//
// FIRST: rendering the product name as TEXT and calling it a logo. A wordmark
// is approved artwork with its own drawing; typing "NubArca" in the heading
// face is a different mark that nobody approved.
//
// SECOND: sizing the FILE instead of the LOCKUP. Both approved binaries now
// share the 480x135 compact frame, but they still do not fill it identically —
// the artwork occupies 98.33% of the on-dark file and 98.54% of the on-light
// one — so a requested width is still divided by the measured ratio. That is
// what keeps `visibleWidth` meaning the width of what a person can see, and
// what keeps the 120 px minimum a real minimum rather than a number in the
// code.
//
// Both files were once very different shapes: the on-light artwork shipped on a
// 1516x1024 canvas at 77.24% width, which cost 1.9 MB to draw a 200 px logo and
// rendered visibly smaller than the dark one at the same width. The approved
// compact rendition removed that difference at the source, in the brand
// package, rather than here in a per-theme layout correction.
//
// Nothing here recolours, redraws, stretches or crops: it picks an approved
// binary and scales it uniformly.

import React from 'react';
import { Image, StyleSheet, View, type StyleProp, type ViewStyle } from 'react-native';
import { useTheme } from './theme';
import type { ThemeName } from './themePreference.ts';

/** Minimum VISIBLE lockup width, from the brand geometry contract. */
export const MIN_WORDMARK_WIDTH = 120;

// Measured from the alpha bounding boxes of the approved binaries. They are
// never modified to make these numbers rounder.
const CONTENT_WIDTH_RATIO: Record<ThemeName, number> = {
  dark: 0.9833,
  light: 0.9854,
};

// One frame for both themes, which is the point of the compact rendition: the
// element box no longer depends on which theme is drawing it.
const FILE_WIDTH = 480;
const FILE_HEIGHT = 135;

const WORDMARK: Record<ThemeName, number> = {
  dark: require('../../assets/brand/nubarca-wordmark-on-dark-480w.png'),
  light: require('../../assets/brand/nubarca-wordmark-on-light-480w.png'),
};

/**
 * Size the element so the VISIBLE lockup is `visibleWidth` wide, in either
 * theme. Pure, so the rule is checkable without rendering anything.
 */
export function lockupLayout(
  theme: ThemeName,
  visibleWidth: number,
): { width: number; height: number } {
  const clamped = Math.max(visibleWidth, MIN_WORDMARK_WIDTH);
  const width = Math.round(clamped / CONTENT_WIDTH_RATIO[theme]);
  return { width, height: Math.round((width * FILE_HEIGHT) / FILE_WIDTH) };
}

export function BrandLockup({
  visibleWidth = MIN_WORDMARK_WIDTH,
  style,
}: {
  /** Width of the VISIBLE lockup, not of the file. Clamped to the minimum. */
  visibleWidth?: number;
  style?: StyleProp<ViewStyle>;
}): React.JSX.Element {
  const { theme } = useTheme();
  const layout = lockupLayout(theme, visibleWidth);
  return (
    // The accessible name lives on the WRAPPER and the image is hidden, so the
    // product is announced exactly once rather than twice.
    <View accessible accessibilityRole="image" accessibilityLabel="NubArca" style={style}>
      <Image
        source={WORDMARK[theme]}
        style={[styles.image, layout]}
        resizeMode="contain"
        accessible={false}
        importantForAccessibility="no"
      />
    </View>
  );
}

const styles = StyleSheet.create({
  image: { alignSelf: 'center' },
});

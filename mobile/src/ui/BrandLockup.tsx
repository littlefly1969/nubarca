// The NubArca wordmark, on either surface (BRAND-LOGO-01).
//
// Two things this component exists to prevent.
//
// FIRST: rendering the product name as TEXT and calling it a logo. A wordmark
// is approved artwork with its own drawing; typing "NubArca" in the heading
// face is a different mark that nobody approved.
//
// SECOND: sizing the FILE instead of the LOCKUP. The two approved binaries put
// the same 3.75:1 lockup on very different canvases — the on-dark files are
// tightly cropped, the on-light one sits on a much larger transparent field. At
// one shared width the light variant would render visibly smaller, and could
// silently fall under the 120 px minimum while the code still said 120. The
// ratios below are the fraction of each file that the artwork occupies, so a
// requested width is the width of what a person can actually see.
//
// Nothing here recolours, redraws, stretches or crops: it picks an approved
// binary and scales it uniformly.

import React from 'react';
import { Image, StyleSheet, View, type StyleProp, type ViewStyle } from 'react-native';
import { useTheme } from './theme';
import type { ThemeName } from './themePreference.ts';

/** Minimum VISIBLE lockup width, from the brand geometry contract. */
export const MIN_WORDMARK_WIDTH = 120;

// Measured from the alpha bounding boxes of the approved binaries, matching
// frontend/src/brand/brand.ts. The binaries are never modified to make these
// numbers rounder.
const CONTENT_WIDTH_RATIO: Record<ThemeName, number> = {
  dark: 0.9833,
  light: 0.7724,
};

// File aspect, which is NOT the lockup's aspect: the element has to match the
// file it draws, or the artwork is stretched.
const WORDMARK: Record<ThemeName, { source: number; fileWidth: number; fileHeight: number }> = {
  dark: {
    source: require('../../assets/brand/nubarca-wordmark-on-dark-480w.png'),
    fileWidth: 480,
    fileHeight: 135,
  },
  light: {
    source: require('../../assets/brand/nubarca-wordmark-on-light.png'),
    fileWidth: 1516,
    fileHeight: 1024,
  },
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
  const { fileWidth, fileHeight } = WORDMARK[theme];
  return { width, height: Math.round((width * fileHeight) / fileWidth) };
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
        source={WORDMARK[theme].source}
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

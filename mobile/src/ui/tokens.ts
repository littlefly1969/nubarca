// Mobile design tokens — the parts of the visual language that do NOT depend
// on the theme: rhythm, shape, type roles, touch targets, grid math.
//
// COLOUR LIVES IN palette.ts AND IS REACHED THROUGH useColors()/themed().
// It is deliberately not re-exported here: a module-level `colors` constant
// cannot follow a theme switch, and having one available made it the path of
// least resistance. Screens consume ONLY the semantic layer, never a hex.

import { Platform } from 'react-native';
import { fontFamilies } from './fonts.ts';

export const spacing = {
  xs: 4,
  s: 8,
  m: 12,
  l: 16,
  xl: 24,
  xxl: 32,
} as const;

// Canonical shape roles (BRAND-GEOMETRY-01). A component may not invent a
// radius: it names the role its shape plays.
export const radius = {
  compact: 8,
  control: 12,
  card: 16,
  hero: 24,
  pill: 999,
} as const;

/**
 * @deprecated Legacy radii, kept only so the screens this slice does not
 * redesign keep compiling. No new call site may use them — use `radius`.
 */
export const radii = {
  s: 6,
  m: 10,
  l: 14,
  round: 999,
} as const;

// Type roles: a FAMILY, a SIZE and a WEIGHT, never a colour. A role that
// carried its own colour could not be reused across a light sheet and a dark
// viewer, and each caller would have had to override it — which is how a role
// stops being one.
//
// Space Grotesk carries display and heading roles, Exo 2 body and UI, per
// BRAND-TYPE-01. Metrics are the mobile column of the Component Language.
// `fontWeight` travels with the family because React Native needs the family to
// pick the face and the weight to keep the intent readable in the style itself.
//
// `allowFontScaling` stays default (true) everywhere: text scaling is an
// accessibility requirement.
export const typography = {
  hero: { fontFamily: fontFamilies.displayBold, fontSize: 32, fontWeight: '700' as const },
  pageTitle: { fontFamily: fontFamilies.displaySemiBold, fontSize: 24, fontWeight: '600' as const },
  sectionTitle: { fontFamily: fontFamilies.displaySemiBold, fontSize: 18, fontWeight: '600' as const },
  body: { fontFamily: fontFamilies.uiRegular, fontSize: 16, fontWeight: '400' as const },
  secondary: { fontFamily: fontFamilies.uiRegular, fontSize: 14, fontWeight: '400' as const },
  label: { fontFamily: fontFamilies.uiMedium, fontSize: 14, fontWeight: '500' as const },
  button: { fontFamily: fontFamilies.uiSemiBold, fontSize: 15, fontWeight: '600' as const },
  badge: { fontFamily: fontFamilies.uiSemiBold, fontSize: 12, fontWeight: '600' as const },
} as const;

/**
 * @deprecated Legacy type roles, carrying neither family nor the brand metrics.
 * Kept so the screens this slice does not redesign keep their current
 * proportions. No new call site may use them — use `typography`.
 */
export const type = {
  title: { fontSize: 22, fontWeight: '700' as const },
  sectionTitle: { fontSize: 15, fontWeight: '700' as const },
  body: { fontSize: 15, fontWeight: '400' as const },
  secondary: { fontSize: 13, fontWeight: '400' as const },
  badge: { fontSize: 11, fontWeight: '600' as const },
};

// Motion (BRAND-MOTION-01). Four durations and one easing, so a transition
// names the kind of movement it is rather than picking a number.
export const motion = {
  durationMs: {
    fast: 120,
    standard: 180,
    navigation: 240,
    deliberate: 320,
  },
  // The canonical curve. React Native's Easing.bezier takes the same control
  // points; the identifier is kept in the token so the value is one thing
  // across web, mobile and TV rather than three transcriptions.
  easing: { standard: 'cubic-bezier(0.2, 0, 0, 1)', bezier: [0.2, 0, 0, 1] as const },
} as const;

// Minimum touch target per Android guidance (48dp class).
export const touch = {
  minSize: 48,
};

export const iconSizes = {
  s: 18,
  m: 22,
  l: 28,
} as const;

// Safe-area conventions: screens use react-native-safe-area-context and keep
// content out of cutouts; edge-to-edge is enabled once, in the root layout.
export const safeArea = {
  // Extra breathing room under headers on Android where a status bar overlaps
  // edge-to-edge layouts before the provider measures insets.
  androidStatusBarPadding: Platform.OS === 'android' ? 8 : 0,
};

// Grid layout math shared by Photos/Videos/Albums so column counts stay
// consistent across tabs. TARGET_TILE is the desired on-edge tile length;
// columns adapt to window width with sane bounds.
export const grid = {
  gap: 2 as number, // dense gallery gutters
  minColumns: 3,
  maxColumns: 5,
  targetTile: 120,
  albumMinColumns: 2,
  albumMaxColumns: 3,
  albumTargetTile: 168,
};

export function columnsForWidth(width: number): number {
  const raw = Math.floor(width / grid.targetTile);
  return Math.min(grid.maxColumns, Math.max(grid.minColumns, raw));
}

export function albumColumnsForWidth(width: number): number {
  const raw = Math.floor(width / grid.albumTargetTile);
  return Math.min(grid.albumMaxColumns, Math.max(grid.albumMinColumns, raw));
}

// Mobile design tokens — the parts of the visual language that do NOT depend
// on the theme: rhythm, shape, type roles, touch targets, grid math.
//
// COLOUR LIVES IN palette.ts AND IS REACHED THROUGH useColors()/themed().
// It is deliberately not re-exported here: a module-level `colors` constant
// cannot follow a theme switch, and having one available made it the path of
// least resistance. Screens consume ONLY the semantic layer, never a hex.

import { Platform } from 'react-native';

export const spacing = {
  xs: 4,
  s: 8,
  m: 12,
  l: 16,
  xl: 24,
  xxl: 32,
} as const;

export const radii = {
  s: 6,
  m: 10,
  l: 14,
  round: 999,
} as const;

// Type roles: a SIZE and a WEIGHT, never a colour. A role that carried its own
// colour could not be reused across a light sheet and a dark viewer, and each
// caller would have had to override it — which is how a role stops being one.
// `allowFontScaling` stays default (true) everywhere: text scaling is an
// accessibility requirement.
export const type = {
  title: { fontSize: 22, fontWeight: '700' as const },
  sectionTitle: { fontSize: 15, fontWeight: '700' as const },
  body: { fontSize: 15, fontWeight: '400' as const },
  secondary: { fontSize: 13, fontWeight: '400' as const },
  badge: { fontSize: 11, fontWeight: '600' as const },
};

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

// Mobile design tokens — one small source of visual truth.
//
// The six base colors are the approved NubArca brand palette (docs/brand.md),
// verbatim. Everything else is a semantic role mapped onto them, mirroring how
// the web frontend maps --brand-* onto semantic tokens. Screens consume ONLY
// the semantic layer.

import { Platform } from 'react-native';

// Brand palette — never referenced directly by screens.
const brand = {
  midnightNavy: '#0A0F1A',
  deepBlue: '#0F1E3A',
  electricBlue: '#1565FF',
  cyanGlow: '#00D4FF',
  softViolet: '#9A6CFF',
  cloudWhite: '#F5F7FB',
} as const;

// Semantic tokens (light chrome; the media viewer uses its own dark surface).
export const colors = {
  // Surfaces
  canvas: brand.cloudWhite,
  surface: '#FFFFFF',
  surfaceMuted: '#EEF1F6',
  separator: '#E2E6EE',

  // Text
  textPrimary: brand.midnightNavy,
  textSecondary: '#5A6472',
  textTertiary: '#9AA3B2',
  textOnAccent: '#FFFFFF',

  // Actions & accents
  accent: brand.electricBlue,
  accentPressed: '#0F51CC',
  accentDisabled: '#B9CBF5',
  focusRing: brand.cyanGlow,

  // Feedback
  danger: '#C63838',
  dangerPressed: '#A32B2B',
  dangerSurface: '#FBEBEB',
  warningSurface: '#FFF6E0',
  warningText: '#7A5B00',

  // Media surfaces (viewer / player are always dark)
  mediaBackground: brand.midnightNavy,
  mediaChrome: 'rgba(10, 15, 26, 0.72)',
  mediaText: brand.cloudWhite,

  // Tile overlays
  overlayScrim: 'rgba(10, 15, 26, 0.55)',
  tilePlaceholder: '#E4E8EF',
} as const;

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

// Type roles, not font sizes per screen. `allowFontScaling` stays default
// (true) everywhere — text scaling is an accessibility requirement.
export const type = {
  title: { fontSize: 22, fontWeight: '700' as const, color: colors.textPrimary },
  sectionTitle: { fontSize: 15, fontWeight: '700' as const, color: colors.textPrimary },
  body: { fontSize: 15, fontWeight: '400' as const, color: colors.textPrimary },
  secondary: { fontSize: 13, fontWeight: '400' as const, color: colors.textSecondary },
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

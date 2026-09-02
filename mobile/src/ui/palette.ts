// The two palettes, and the constants that belong to neither.
//
// AUTHORITY: docs/brand.md for the six brand hexes and the two deliberate
// accent tints; frontend/src/styles.css for the semantic roles they map onto.
// palette.test.ts reads the brand document and fails if this file drifts from
// it, so the mapping cannot quietly become a second opinion about the brand.
//
// THE STRUCTURE IS THE POINT:
//
//   * `Palette` — everything that CHANGES with the theme. Reached through
//     useColors(); never imported as a module-level constant, because a value
//     captured at import time cannot follow a theme switch.
//   * `media` — everything that does NOT. The viewer, the player and the
//     overlays burned onto a thumbnail are always dark, in both themes: they
//     frame the content instead of the app, and a photo does not become a
//     light-mode photo. Screens import these directly, and that is correct.
//
// No component may introduce a colour of its own (docs/brand.md).

// Brand palette — the approved hexes, verbatim. Never referenced by screens.
const brand = {
  midnightNavy: '#0A0F1A',
  deepBlue: '#0F1E3A',
  electricBlue: '#1565FF',
  cyanGlow: '#00D4FF',
  softViolet: '#9A6CFF',
  cloudWhite: '#F5F7FB',
} as const;

export interface Palette {
  // Surfaces, back to front.
  canvas: string;
  surface: string;
  surfaceMuted: string;
  separator: string;

  // Text.
  textPrimary: string;
  textSecondary: string;
  textTertiary: string;
  /** Text and iconography placed ON an accent fill. */
  textOnAccent: string;

  // Actions.
  /**
   * Accent TEXT and borders. A legibility tint, not the brand hex — see
   * docs/brand.md. Never use it as a FILL: white on the dark tint is only
   * 3.6:1, which is the exact regression the document was written to prevent.
   */
  accent: string;
  /** Accent FILLS, with `textOnAccent` on top. Electric Blue itself in dark. */
  accentStrong: string;
  /** A wash of the accent: selected chips, highlighted rows. */
  accentSubtle: string;
  accentDisabled: string;

  // Feedback.
  danger: string;
  dangerSurface: string;
  warningSurface: string;
  warningText: string;

  // Placeholder shown while a thumbnail loads.
  tilePlaceholder: string;

  /** Backdrop behind a modal or a bottom sheet. */
  scrim: string;
}

// Light: Cloud White canvas, Midnight Navy text, and the deepened Electric Blue
// the brand document mandates so accent TEXT clears WCAG AA on white — the
// official #1565FF reaches only 4.0:1 there.
export const lightPalette: Palette = {
  canvas: brand.cloudWhite,
  surface: '#FFFFFF',
  surfaceMuted: '#E7ECF5',
  separator: '#C9D4E6',

  textPrimary: brand.midnightNavy,
  textSecondary: '#36455F',
  textTertiary: '#596884',
  textOnAccent: '#FFFFFF',

  accent: '#0B4FD6',
  accentStrong: '#0B4FD6',
  accentSubtle: '#DCE6FA',
  accentDisabled: '#B9CBF5',

  danger: '#C21127',
  dangerSurface: '#FBE9EC',
  warningSurface: '#FFF6E0',
  warningText: '#7A5B00',

  tilePlaceholder: '#E4E8EF',

  scrim: 'rgba(10, 15, 26, 0.45)',
};

// Dark: Midnight Navy canvas, Deep Blue raised surfaces, and Electric Blue
// lifted to 5.3:1 on the canvas for the same reason, in the other direction.
export const darkPalette: Palette = {
  canvas: brand.midnightNavy,
  surface: brand.deepBlue,
  surfaceMuted: '#132546',
  separator: '#22355C',

  textPrimary: brand.cloudWhite,
  textSecondary: '#B6C4DC',
  textTertiary: '#8FA0BE',
  textOnAccent: '#FFFFFF',

  accent: '#3D82FF',
  // Electric Blue itself, verbatim: white clears AA on it, and it is the fill
  // the brand document reserves for exactly this.
  accentStrong: '#1565FF',
  // An 18% wash of Electric Blue over the canvas: dark enough that accent TEXT
  // on it clears AA (4.55:1). A lighter, more obviously tinted wash reads
  // better as a shape and worse as a label — the chips carry a hairline accent
  // border so the shape does not have to come from the fill.
  accentSubtle: '#0C1E43',
  accentDisabled: '#2A4172',

  // Destructive stays warm-red rather than becoming another shade of the brand
  // blue: it has to read as ITSELF (docs/brand.md).
  danger: '#FF7A85',
  dangerSurface: '#2E1418',
  warningSurface: '#332A12',
  warningText: '#F0D48A',

  tilePlaceholder: '#16233D',

  // Darker than the light one: a dim room needs less of a veil to separate a
  // sheet from what is behind it.
  scrim: 'rgba(2, 6, 16, 0.6)',
};

export const palettes: Record<'dark' | 'light', Palette> = {
  dark: darkPalette,
  light: lightPalette,
};

/**
 * Theme-INDEPENDENT surfaces: the media viewer, the player, and the overlays
 * drawn on top of a thumbnail. Always dark, in both themes.
 *
 * These are safe to import as module constants precisely because they never
 * change — the rule that forbids it for `Palette` does not apply here.
 */
export const media = {
  /** The viewer and player background. */
  background: brand.midnightNavy,
  /** Text and icons drawn on media or on a scrim. */
  text: brand.cloudWhite,
  textSecondary: 'rgba(255, 255, 255, 0.75)',
  /** A control's own surface, floating over media. */
  chrome: 'rgba(10, 15, 26, 0.72)',
  chromeButton: 'rgba(255, 255, 255, 0.12)',
  /** The veil that makes a badge legible over an unknown photo. */
  scrim: 'rgba(10, 15, 26, 0.55)',
  scrimSoft: 'rgba(10, 15, 26, 0.25)',
  scrimStrong: 'rgba(10, 15, 26, 0.6)',
  /** Soft Violet, the brand's LIMITED highlight — never a primary action. */
  highlight: 'rgba(154, 108, 255, 0.9)',
} as const;

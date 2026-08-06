import type { EffectiveTheme } from '../theme/themePreference';

// The product's identity, in one place.
//
// Capitalization is part of the brand contract: it is always "NubArca", with a
// capital A. Never "Nubarca", "NUBARCA" or "Nub Arca", and never the former
// name. Brand names are NOT translated, so these are plain constants rather
// than i18n keys — every locale renders the identical string. (`app.name` still
// exists in the locale files and resolves to PRODUCT_NAME, so the existing
// translation call sites keep working.)

export const PRODUCT_NAME = 'NubArca';

/** The television product. Also untranslated. */
export const TV_PRODUCT_NAME = 'NubArca TV';

/**
 * Runtime brand artwork.
 *
 * Every file here is a byte-exact copy of an approved asset from the canonical
 * package at `assets/brand/nubarca/`, placed in `public/brand/` by
 * `scripts/sync-brand-assets.py`. Nothing in the frontend resizes, recolours or
 * regenerates artwork, and no reference board is ever served.
 *
 * Two rules the picker below exists to enforce:
 *
 *  1. SMALL UI CONTEXTS (16–48 px: shell, navigation, drawer) use the FLAT
 *     mark. The launcher/PWA icon is luminous and framed; at 24 px its glow
 *     turns to mush and its frame competes with the surrounding chrome. It is
 *     an app icon, not a UI icon, and must never be used as one.
 *  2. The mark and the wordmark each ship an ON-DARK and an ON-LIGHT variant.
 *     The dark-surface artwork carries Cloud White; the light-surface artwork
 *     carries Midnight Navy. Showing the light-surface wordmark on Midnight
 *     Navy (or the reverse) is unreadable, so the variant is chosen from the
 *     resolved theme rather than left to chance.
 */

/** Sizes of the flat mark available in `public/brand/`, ascending. */
const FLAT_MARK_SIZES = [16, 24, 32, 48, 64, 128, 256] as const;

/**
 * Fraction of a flat-mark file that the artwork actually occupies.
 *
 * The approved master draws the symbol on a canvas far larger than itself:
 * 528×476 of 1024×1024, so only 51.6% of the width and 46.5% of the height
 * were ink. Every derivative inherited that, which is why a 16px favicon
 * rendered ~10×8px of symbol and read as undersized however large the CSS box
 * around it was made — the empty pixels scaled with the artwork.
 *
 * `scripts/generate-compact-brand-marks.py` now crops that transparent excess
 * once, at the source, and re-pads to a 603px square with a uniform safe
 * margin (~1 physical pixel at 16px). Same geometry, same colours, no redraw.
 *
 * These are the resulting occupancy ratios. They exist so callers can size the
 * VISIBLE mark rather than its box: a 41px box renders ~36px of artwork.
 */
export const MARK_CONTENT_RATIO = { width: 528 / 603, height: 476 / 603 } as const;

/** Box size whose VISIBLE artwork is `visibleWidthPx` wide. */
export function markBoxForVisibleWidth(visibleWidthPx: number): number {
  return Math.round(visibleWidthPx / MARK_CONTENT_RATIO.width);
}

/**
 * Visible mark width in the app shell, per the UX-02 measurement.
 *
 * These are VISIBLE artwork widths, not box sizes — `markBoxForVisibleWidth`
 * converts. The mobile step-down is applied in CSS (one topbar serves both
 * widths), so the component is always given the desktop box and CSS narrows
 * it; asking for the larger box also picks the sharper asset for both.
 */
export const SHELL_MARK_VISIBLE_PX = { desktop: 36, mobile: 32 } as const;

/**
 * The approved flat mark for a UI slot, at the smallest shipped size that still
 * covers the rendered box on a 2× display — so a 24 px slot loads the 48 px
 * asset and stays crisp without pulling a 256 px file into a navigation bar.
 */
export function flatMarkUrl(theme: EffectiveTheme, renderedPx: number): string {
  const needed = renderedPx * 2;
  const size = FLAT_MARK_SIZES.find((s) => s >= needed) ?? FLAT_MARK_SIZES.at(-1)!;
  return `/brand/nubarca-mark-flat-on-${theme}-${size}.png`;
}

/** Wordmark widths shipped for dark surfaces, ascending. */
const WORDMARK_DARK_WIDTHS = [480, 960, 1440] as const;

/**
 * Fraction of each wordmark file's width that the artwork actually occupies.
 *
 * The dark-surface files are tightly cropped (98.3%); the approved light-surface
 * file is the same lockup on a much larger transparent canvas (77.2%). Both
 * carry an identical 3.75:1 content shape. Sizing the `<img>` directly would
 * therefore render the light variant visibly smaller than the dark one at the
 * same CSS width — and could silently drop it below the 120 px minimum. The
 * component divides by this ratio so a requested width is the width of the
 * VISIBLE LOCKUP in both themes.
 *
 * Measured from the alpha bounding boxes; the binaries are approved and are
 * never modified to make the numbers rounder.
 */
const WORDMARK_CONTENT_WIDTH_RATIO: Record<EffectiveTheme, number> = {
  dark: 0.9833,
  light: 0.7724,
};

export interface WordmarkAsset {
  src: string;
  /** Width to set on the element so the visible lockup is `contentWidthPx`. */
  elementWidthPx: number;
}

/**
 * The approved wordmark for the resolved theme, sized so the VISIBLE lockup is
 * `contentWidthPx` wide. The light-surface wordmark ships in a single
 * resolution; the dark-surface one is responsive.
 */
export function wordmarkAsset(theme: EffectiveTheme, contentWidthPx: number): WordmarkAsset {
  const elementWidthPx = Math.round(contentWidthPx / WORDMARK_CONTENT_WIDTH_RATIO[theme]);
  if (theme === 'light') {
    return { src: '/brand/nubarca-wordmark-on-light.png', elementWidthPx };
  }
  const needed = elementWidthPx * 2;
  const width = WORDMARK_DARK_WIDTHS.find((w) => w >= needed) ?? WORDMARK_DARK_WIDTHS.at(-1)!;
  return { src: `/brand/nubarca-wordmark-on-dark-${width}w.png`, elementWidthPx };
}

/** Fixed-name assets referenced from `index.html` and the web manifest. */
export const BRAND_ASSETS = {
  favicon: '/brand/favicon.ico',
  faviconPng32: '/brand/favicon-32.png',
  appleTouchIcon: '/brand/nubarca-apple-touch-icon-180.png',
  pwa192: '/brand/nubarca-pwa-192.png',
  pwa512: '/brand/nubarca-pwa-512.png',
  pwaMaskable512: '/brand/nubarca-pwa-maskable-512.png',
} as const;

/** Minimum rendered wordmark width, from the brand development guidelines. */
export const MIN_WORDMARK_WIDTH_PX = 120;

/** Minimum rendered icon size, from the brand development guidelines. */
export const MIN_ICON_SIZE_PX = 24;

/** Logo clear space, as a fraction of the rendered logo height. */
export const LOGO_CLEAR_SPACE_RATIO = 0.25;

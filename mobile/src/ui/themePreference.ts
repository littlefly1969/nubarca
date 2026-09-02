// What theme the app shows, and how a stored choice resolves to one.
//
// AUTHORITY: frontend/src/theme/themePreference.ts. This is the SAME rule the
// web uses, deliberately: a product with one brand does not get to disagree
// with itself about what "system" means or which theme a new install starts in.
// The values are asserted against docs/brand.md in palette.test.ts.
//
// The module is pure — no React, no storage, no platform. That is what makes
// the rule testable on a laptop, and what will let a third client (iOS) adopt
// it without inheriting Android's plumbing.

export type ThemePreference = 'dark' | 'light' | 'system';

/** The theme actually rendered, once `system` has been resolved. */
export type ThemeName = 'dark' | 'light';

// Dark is the product default (docs/brand.md §Themes): a user with no stored
// preference gets dark, and so does a user whose stored value is unreadable.
export const DEFAULT_THEME_PREFERENCE: ThemePreference = 'dark';

/** Display order of the choice control, matching the web's. */
export const THEME_PREFERENCES: readonly ThemePreference[] = ['dark', 'light', 'system'];

/** Narrow an untrusted value (secure storage, props) to a preference. */
export function toThemePreference(raw: unknown): ThemePreference | null {
  return raw === 'dark' || raw === 'light' || raw === 'system' ? raw : null;
}

/**
 * Resolve a preference against what the OS reports.
 *
 * `systemScheme` is React Native's useColorScheme() value, which is null while
 * the OS answer is unknown. An unknown answer falls back to the PRODUCT
 * default, never to light — the same reason the web asks for
 * `prefers-color-scheme: light` rather than dark.
 *
 * An explicit choice always wins. The OS is consulted only for `system`, so a
 * phone in dark mode can never override a user who deliberately chose light.
 */
export function resolveTheme(
  preference: ThemePreference,
  systemScheme: 'dark' | 'light' | null | undefined,
): ThemeName {
  if (preference !== 'system') return preference;
  if (systemScheme === 'dark' || systemScheme === 'light') return systemScheme;
  return DEFAULT_THEME_PREFERENCE === 'system' ? 'dark' : DEFAULT_THEME_PREFERENCE;
}

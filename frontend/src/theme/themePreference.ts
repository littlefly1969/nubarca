// Pure theme-preference model. No React, no DOM writes beyond the single
// documented `applyEffectiveTheme` helper, so the rules below are unit-testable
// and are shared verbatim by the pre-render bootstrap in index.html.
//
// Storage is LOCAL ONLY (no backend field). The key holds one of exactly three
// bounded tokens; anything else — a stale value, a value written by a future
// version, a hostile value — reads back as "absent", so the default applies
// instead of leaking an unknown string into the DOM.

// Versioned key: a future incompatible preference shape gets `.v2`, so old
// values are ignored rather than misinterpreted.
export const THEME_STORAGE_KEY = 'nubarca.theme';

// What the user chose. `system` follows the OS/browser preference live.
export type ThemePreference = 'dark' | 'light' | 'system';

// What actually gets painted. `system` is always resolved before this point.
export type EffectiveTheme = 'dark' | 'light';

// Dark is the product default: a user with no stored preference gets dark, and
// so does a user whose stored value is unreadable.
export const DEFAULT_THEME_PREFERENCE: ThemePreference = 'dark';

// Display order of the choice control.
export const THEME_PREFERENCES: readonly ThemePreference[] = ['dark', 'light', 'system'];

// The media query the `system` preference follows. We ask for LIGHT (rather
// than dark) so that "no preference" — where neither query matches — resolves
// to dark, keeping the product default intact.
export const PREFERS_LIGHT_QUERY = '(prefers-color-scheme: light)';

// Narrow an untrusted value (localStorage, URL, props) to a preference.
export function toThemePreference(raw: unknown): ThemePreference | null {
  return raw === 'dark' || raw === 'light' || raw === 'system' ? raw : null;
}

// Read the stored preference, falling back to the default for a missing,
// invalid or unreadable (private-mode) value.
export function readStoredThemePreference(): ThemePreference {
  if (typeof window === 'undefined') return DEFAULT_THEME_PREFERENCE;
  try {
    return toThemePreference(window.localStorage.getItem(THEME_STORAGE_KEY))
      ?? DEFAULT_THEME_PREFERENCE;
  } catch {
    // localStorage can throw outright (private mode, blocked storage).
    return DEFAULT_THEME_PREFERENCE;
  }
}

// Persist the preference. A storage failure is not fatal — the in-memory
// choice still applies for this session.
export function writeStoredThemePreference(preference: ThemePreference): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(THEME_STORAGE_KEY, preference);
  } catch {
    // Ignore: the session keeps the choice, it just will not survive a reload.
  }
}

// The single place that turns a preference + the OS signal into a paint.
export function resolveEffectiveTheme(
  preference: ThemePreference,
  prefersLight: boolean,
): EffectiveTheme {
  if (preference === 'system') return prefersLight ? 'light' : 'dark';
  return preference;
}

// Whether the browser currently reports a LIGHT system preference. False when
// matchMedia is unavailable (jsdom without a stub, very old browsers), which
// keeps `system` on the dark default rather than guessing.
export function systemPrefersLight(): boolean {
  if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return false;
  try {
    return window.matchMedia(PREFERS_LIGHT_QUERY).matches;
  } catch {
    return false;
  }
}

// Stamp the effective theme where CSS can see it. This is the ONLY contract
// between the bootstrap script and the React provider: both write the same
// attribute with the same value, so the provider taking ownership after mount
// cannot change what is already on screen.
export function applyEffectiveTheme(theme: EffectiveTheme): void {
  if (typeof document === 'undefined') return;
  document.documentElement.dataset.theme = theme;
}

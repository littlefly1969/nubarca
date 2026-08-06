// Bounded, failure-tolerant reads of the browser-storage keys the app owns.
//
// Every key lives under the `nubarca.` prefix. Storage can be unavailable
// outright (private mode, blocked third-party storage, a hostile embedding), so
// a read never throws — it reports "absent" and the caller applies its default.
//
// The theme preference deliberately does NOT use this helper: its rules are
// duplicated verbatim in the pre-paint bootstrap in index.html, which cannot
// import a module. See src/theme/themePreference.ts.

/**
 * Read `key` from localStorage. Returns null when the key holds no value, or
 * when storage is unavailable.
 */
export function readStoredItem(key: string): string | null {
  if (typeof window === 'undefined') return null;
  try {
    return window.localStorage.getItem(key);
  } catch {
    // Blocked storage / private mode: the caller falls back to its default.
    return null;
  }
}

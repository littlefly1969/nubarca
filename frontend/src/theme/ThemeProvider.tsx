import { useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { ThemeContext, type ThemeContextValue } from './ThemeContext';
import {
  PREFERS_LIGHT_QUERY,
  applyEffectiveTheme,
  readStoredThemePreference,
  resolveEffectiveTheme,
  systemPrefersLight,
  writeStoredThemePreference,
  type ThemePreference,
} from './themePreference';

// Owns the theme preference after the first paint.
//
// The pre-render bootstrap in index.html has already stamped
// `<html data-theme>` from the SAME stored preference and the SAME resolution
// rules, so mounting this provider re-applies the value that is already on
// screen — there is no light-theme flash and no post-mount flip. From here on
// the provider is authoritative: it persists explicit choices and, while the
// preference is `system`, follows live OS changes.

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreference] = useState<ThemePreference>(readStoredThemePreference);
  // Tracked separately from `preference` so an OS change while on `system`
  // re-renders without rewriting storage.
  const [prefersLight, setPrefersLight] = useState<boolean>(systemPrefersLight);

  // Follow the OS while (and only while) the preference is `system`. The
  // listener is always attached — a `system` switch then needs no remount — but
  // the resolved value below ignores it for the explicit preferences.
  useEffect(() => {
    if (typeof window === 'undefined' || typeof window.matchMedia !== 'function') return;
    let query: MediaQueryList;
    try {
      query = window.matchMedia(PREFERS_LIGHT_QUERY);
    } catch {
      return;
    }
    const onChange = (e: MediaQueryListEvent) => setPrefersLight(e.matches);
    // addEventListener is the modern API; addListener is the pre-2019 fallback
    // some embedded browsers (and older jsdom stubs) still only expose.
    if (typeof query.addEventListener === 'function') {
      query.addEventListener('change', onChange);
      return () => query.removeEventListener('change', onChange);
    }
    if (typeof query.addListener === 'function') {
      query.addListener(onChange);
      return () => query.removeListener(onChange);
    }
    return;
  }, []);

  const effective = resolveEffectiveTheme(preference, prefersLight);

  // Keep the DOM attribute in sync. On mount this writes the value the
  // bootstrap already wrote, so nothing visibly changes.
  useEffect(() => {
    applyEffectiveTheme(effective);
  }, [effective]);

  const choose = useCallback((next: ThemePreference) => {
    setPreference(next);
    writeStoredThemePreference(next);
    // Re-read the OS signal on every explicit choice so selecting `system`
    // resolves against the CURRENT preference even if no change event has
    // fired since mount.
    setPrefersLight(systemPrefersLight());
  }, []);

  const value = useMemo<ThemeContextValue>(
    () => ({ preference, effective, setPreference: choose }),
    [preference, effective, choose],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

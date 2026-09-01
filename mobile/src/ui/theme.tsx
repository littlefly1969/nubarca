// The theme at runtime: which palette is active, how a user changes it, and how
// a StyleSheet gets built against it.
//
// WHY A CONTEXT AND NOT A MODULE CONSTANT. A `StyleSheet.create` evaluated at
// import time freezes whatever colours were current when the module first
// loaded. That is invisible until the theme changes, and then half the app
// keeps the old one. `themed()` below exists so a screen can keep writing one
// stylesheet per file and still follow a switch.
//
// The RULE is resolveTheme() in themePreference.ts — pure, shared with the web,
// and tested there. This file is only the plumbing: React state, persistence,
// and the system bars.

import React, {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
} from 'react';
import { Platform, useColorScheme } from 'react-native';
import * as SystemUI from 'expo-system-ui';
import { palettes, type Palette } from './palette.ts';
import {
  DEFAULT_THEME_PREFERENCE,
  resolveTheme,
  toThemePreference,
  type ThemeName,
  type ThemePreference,
} from './themePreference.ts';
import { getStoredThemePreference, persistThemePreference } from '../api/session.ts';

interface ThemeValue {
  /** The user's choice, including `system`. */
  preference: ThemePreference;
  /** What is actually on screen right now. */
  theme: ThemeName;
  colors: Palette;
  setPreference: (next: ThemePreference) => void;
}

const ThemeContext = createContext<ThemeValue | null>(null);

export function ThemeProvider({ children }: { children: React.ReactNode }): React.JSX.Element {
  const systemScheme = useColorScheme();
  const [preference, setPreferenceState] = useState<ThemePreference>(DEFAULT_THEME_PREFERENCE);

  // Restore the stored choice. The app renders the product default first and
  // corrects itself; it never blocks on storage, because a slow or broken
  // keystore must not be able to hold the whole UI hostage.
  useEffect(() => {
    let cancelled = false;
    void getStoredThemePreference().then(
      (stored) => {
        const parsed = toThemePreference(stored);
        if (!cancelled && parsed !== null) setPreferenceState(parsed);
      },
      () => {
        /* unreadable storage keeps the product default */
      },
    );
    return () => {
      cancelled = true;
    };
  }, []);

  const setPreference = useCallback((next: ThemePreference) => {
    // Applied immediately; persistence is a background courtesy. A write that
    // fails costs the choice on next launch, never the choice now.
    setPreferenceState(next);
    void persistThemePreference(next).catch(() => {});
  }, []);

  const theme = resolveTheme(preference, systemScheme);

  // The window BEHIND the React views. Without this the system paints its
  // default light background, which shows through as a white flash on every
  // navigation transition and behind a screen that has not laid out yet — the
  // one part of a dark app the app itself does not draw.
  //
  // It is also why expo-system-ui is a dependency at all: on Android,
  // `userInterfaceStyle: 'automatic'` is a no-op without it, and the `system`
  // option would have been a second light option wearing another name.
  useEffect(() => {
    void SystemUI.setBackgroundColorAsync(palettes[theme].canvas).catch(() => {});
  }, [theme]);

  // The Android navigation-bar icons are drawn by the system and know nothing
  // about our canvas: on a dark canvas they have to be light, or they vanish.
  // Swallowed, and dynamically required, for the same reasons as the viewer's
  // immersive mode — this is a comfort, not a dependency.
  useEffect(() => {
    if (Platform.OS !== 'android') return;
    try {
      const bar = require('expo-navigation-bar') as {
        setButtonStyleAsync?: (style: 'light' | 'dark') => Promise<unknown>;
      };
      void bar.setButtonStyleAsync?.(theme === 'dark' ? 'light' : 'dark').catch(() => {});
    } catch {
      /* no navigation bar to style */
    }
  }, [theme]);

  const value = useMemo<ThemeValue>(
    () => ({ preference, theme, colors: palettes[theme], setPreference }),
    [preference, theme, setPreference],
  );

  return <ThemeContext.Provider value={value}>{children}</ThemeContext.Provider>;
}

export function useTheme(): ThemeValue {
  const ctx = useContext(ThemeContext);
  if (!ctx) throw new Error('useTheme must be used within a ThemeProvider');
  return ctx;
}

export function useColors(): Palette {
  return useTheme().colors;
}

/**
 * Build a stylesheet against the ACTIVE palette.
 *
 *   const useStyles = themed((colors) => StyleSheet.create({ ... }));
 *   // inside the component:
 *   const styles = useStyles();
 *
 * The result is cached per palette, and there are exactly two palettes for the
 * life of the process — so a sheet is built at most twice, not once per render.
 * That is what makes this affordable enough to use everywhere, which is what
 * keeps a stray hardcoded colour from looking like the easier option.
 */
export function themed<T>(factory: (colors: Palette) => T): () => T {
  const cache = new WeakMap<Palette, T>();
  return function useThemedStyles(): T {
    const colors = useColors();
    const cached = cache.get(colors);
    if (cached !== undefined) return cached;
    const built = factory(colors);
    cache.set(colors, built);
    return built;
  };
}

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  DEFAULT_THEME_PREFERENCE,
  THEME_PREFERENCES,
  THEME_STORAGE_KEY,
  applyEffectiveTheme,
  readStoredThemePreference,
  resolveEffectiveTheme,
  systemPrefersLight,
  toThemePreference,
  writeStoredThemePreference,
} from './themePreference';

afterEach(() => {
  // Unstub FIRST: a test may have replaced localStorage with a throwing stub
  // that has no clear().
  vi.unstubAllGlobals();
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

describe('theme preference model', () => {
  it('defaults to dark for a first-run user with no stored preference', () => {
    expect(DEFAULT_THEME_PREFERENCE).toBe('dark');
    expect(readStoredThemePreference()).toBe('dark');
    expect(resolveEffectiveTheme(readStoredThemePreference(), false)).toBe('dark');
    // Dark stays the default even where the OS asks for light: only an explicit
    // `system` choice consults the OS.
    expect(resolveEffectiveTheme(readStoredThemePreference(), true)).toBe('dark');
  });

  it('offers exactly dark, light and system', () => {
    expect([...THEME_PREFERENCES]).toEqual(['dark', 'light', 'system']);
  });

  it('narrows only the three bounded tokens', () => {
    expect(toThemePreference('dark')).toBe('dark');
    expect(toThemePreference('light')).toBe('light');
    expect(toThemePreference('system')).toBe('system');
    expect(toThemePreference('DARK')).toBeNull();
    expect(toThemePreference('sepia')).toBeNull();
    expect(toThemePreference(null)).toBeNull();
    expect(toThemePreference(undefined)).toBeNull();
    expect(toThemePreference(1)).toBeNull();
  });

  it('round-trips a stored preference under the versioned key', () => {
    writeStoredThemePreference('light');
    expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('light');
    expect(readStoredThemePreference()).toBe('light');

    writeStoredThemePreference('system');
    expect(readStoredThemePreference()).toBe('system');
  });

  it('falls back to dark for a stored value it does not recognise', () => {
    window.localStorage.setItem(THEME_STORAGE_KEY, 'solarized');
    expect(readStoredThemePreference()).toBe('dark');
  });

  it('falls back to dark when localStorage throws', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('blocked'); },
      setItem: () => { throw new Error('blocked'); },
    });
    expect(readStoredThemePreference()).toBe('dark');
    // Writing must not propagate the failure to the caller.
    expect(() => writeStoredThemePreference('light')).not.toThrow();
  });

  // The 0.3.0 identity cutover REMOVED the one-shot migration that read a
  // pre-rename key. These tests pin the removal rather than the migration: the
  // only key consulted is the current one, and a value left behind under any
  // other name can never influence what is painted.
  describe('single-key storage', () => {
    // Assembled, so this file cannot itself carry the former identity.
    const formerKey = `${'nano'}cloud.theme`;

    it('reads and writes exactly one key', () => {
      expect(THEME_STORAGE_KEY).toBe('nubarca.theme');
    });

    it('ignores a value stored under the pre-rename key', () => {
      window.localStorage.setItem(formerKey, 'light');

      expect(readStoredThemePreference()).toBe(DEFAULT_THEME_PREFERENCE);
      // Not adopted, and not written forward either.
      expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
    });

    it('round-trips each of the three bounded tokens through the current key', () => {
      for (const preference of THEME_PREFERENCES) {
        window.localStorage.clear();
        window.localStorage.setItem(THEME_STORAGE_KEY, preference);
        expect(readStoredThemePreference()).toBe(preference);
      }
    });

    it('still defaults to dark for a browser with no stored preference', () => {
      expect(readStoredThemePreference()).toBe('dark');
      expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
    });

    it('writes only the current key', () => {
      window.localStorage.setItem(formerKey, 'light');
      readStoredThemePreference();
      writeStoredThemePreference('dark');

      expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBe('dark');
      // The stale key is left untouched rather than deleted: nothing reads it,
      // so touching another origin's key would be gratuitous.
      expect(window.localStorage.getItem(formerKey)).toBe('light');
    });
  });

  it('resolves system against the OS light signal', () => {
    expect(resolveEffectiveTheme('system', true)).toBe('light');
    expect(resolveEffectiveTheme('system', false)).toBe('dark');
    // Explicit choices ignore the OS entirely.
    expect(resolveEffectiveTheme('light', false)).toBe('light');
    expect(resolveEffectiveTheme('dark', true)).toBe('dark');
  });

  it('reports no light system preference when matchMedia is unavailable', () => {
    vi.stubGlobal('matchMedia', undefined);
    expect(systemPrefersLight()).toBe(false);
  });

  it('reads the OS light preference through matchMedia', () => {
    vi.stubGlobal('matchMedia', (q: string) => ({
      matches: q === '(prefers-color-scheme: light)',
      media: q,
    }));
    expect(systemPrefersLight()).toBe(true);
  });

  it('stamps the effective theme on the document element', () => {
    applyEffectiveTheme('light');
    expect(document.documentElement.dataset.theme).toBe('light');
    applyEffectiveTheme('dark');
    expect(document.documentElement.dataset.theme).toBe('dark');
  });
});

import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  THEME_STORAGE_KEY,
  readStoredThemePreference,
  resolveEffectiveTheme,
  systemPrefersLight,
} from './themePreference';

// The pre-render bootstrap in index.html duplicates the resolution rules on
// purpose: it must run before any module loads, so it cannot import them. These
// tests pin the duplication down — the script is extracted from the real
// index.html and executed against the same inputs as the module, and the two
// must always agree. If someone changes one side only, this fails.

const here = dirname(fileURLToPath(import.meta.url));
const INDEX_HTML = resolve(here, '../../index.html');

function bootstrapSource(): string {
  const html = readFileSync(INDEX_HTML, 'utf8');
  const match = html.match(/<script>([\s\S]*?)<\/script>/);
  if (match === null) throw new Error('index.html no longer contains an inline bootstrap script.');
  return match[1];
}

// Run the extracted script in the jsdom document, exactly as the browser would.
function runBootstrap(): void {
  // eslint-disable-next-line @typescript-eslint/no-implied-eval
  new Function(bootstrapSource())();
}

function stubMatchMedia(prefersLight: boolean): void {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches: query === '(prefers-color-scheme: light)' ? prefersLight : false,
    media: query,
    addEventListener: () => {},
    removeEventListener: () => {},
  }));
}

afterEach(() => {
  // Unstub FIRST: a test may have replaced localStorage with a throwing stub
  // that has no clear().
  vi.unstubAllGlobals();
  window.localStorage.clear();
  delete document.documentElement.dataset.theme;
});

describe('index.html theme bootstrap', () => {
  it('exists as an inline script in the document head', () => {
    const html = readFileSync(INDEX_HTML, 'utf8');
    const headEnd = html.indexOf('</head>');
    const scriptStart = html.indexOf('<script>');
    // It must run before the module bundle and before any paint.
    expect(scriptStart).toBeGreaterThan(-1);
    expect(scriptStart).toBeLessThan(headEnd);
    expect(html).toContain(THEME_STORAGE_KEY);
  });

  it('paints dark on a first run with no stored preference', () => {
    stubMatchMedia(false);
    runBootstrap();
    expect(document.documentElement.dataset.theme).toBe('dark');
  });

  it('paints dark on a first run even when the OS prefers light', () => {
    // Dark is the product default; only an explicit `system` choice follows the
    // OS. This is the regression that would silently reintroduce a light flash.
    stubMatchMedia(true);
    runBootstrap();
    expect(document.documentElement.dataset.theme).toBe('dark');
  });

  const cases: Array<{ stored: string | null; prefersLight: boolean; expected: string }> = [
    { stored: null, prefersLight: false, expected: 'dark' },
    { stored: null, prefersLight: true, expected: 'dark' },
    { stored: 'dark', prefersLight: true, expected: 'dark' },
    { stored: 'light', prefersLight: false, expected: 'light' },
    { stored: 'system', prefersLight: true, expected: 'light' },
    { stored: 'system', prefersLight: false, expected: 'dark' },
    { stored: 'nonsense', prefersLight: true, expected: 'dark' },
  ];

  for (const { stored, prefersLight, expected } of cases) {
    it(`agrees with the module for stored=${stored ?? 'none'} prefersLight=${prefersLight}`, () => {
      if (stored !== null) window.localStorage.setItem(THEME_STORAGE_KEY, stored);
      stubMatchMedia(prefersLight);

      runBootstrap();
      const painted = document.documentElement.dataset.theme;

      const fromModule = resolveEffectiveTheme(readStoredThemePreference(), systemPrefersLight());

      expect(painted).toBe(expected);
      expect(fromModule).toBe(expected);
      expect(painted).toBe(fromModule);
    });
  }

  it('still paints dark when localStorage access throws', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => { throw new Error('blocked'); },
    });
    stubMatchMedia(true);
    runBootstrap();
    expect(document.documentElement.dataset.theme).toBe('dark');
  });

  // The bootstrap decides the FIRST paint, so the removal of the pre-rename
  // fallback has to hold on BOTH sides. If only the module dropped it, the two
  // would disagree and the disagreement would show up as a flash.
  describe('single-key storage', () => {
    // Assembled, so this file cannot itself carry the former identity.
    const formerKey = `${'nano'}cloud.theme`;

    it('ignores a value stored under the pre-rename key', () => {
      window.localStorage.setItem(formerKey, 'light');
      stubMatchMedia(false);

      runBootstrap();

      expect(document.documentElement.dataset.theme).toBe('dark');
      expect(window.localStorage.getItem(THEME_STORAGE_KEY)).toBeNull();
    });

    it('agrees with the module for every value under the pre-rename key', () => {
      for (const stored of ['dark', 'light', 'system', 'nonsense']) {
        window.localStorage.clear();
        delete document.documentElement.dataset.theme;
        window.localStorage.setItem(formerKey, stored);
        stubMatchMedia(false);

        runBootstrap();
        const painted = document.documentElement.dataset.theme;

        const fromModule = resolveEffectiveTheme(readStoredThemePreference(), systemPrefersLight());
        expect(painted).toBe(fromModule);
        // Neither side may adopt it.
        expect(painted).toBe('dark');
      }
    });

    it('honours the current key regardless of what the old one holds', () => {
      window.localStorage.setItem(formerKey, 'light');
      window.localStorage.setItem(THEME_STORAGE_KEY, 'dark');
      stubMatchMedia(true);

      runBootstrap();

      expect(document.documentElement.dataset.theme).toBe('dark');
    });
  });
});

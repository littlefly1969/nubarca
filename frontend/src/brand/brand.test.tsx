import { readFileSync, existsSync, readdirSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { basename, dirname, resolve } from 'node:path';
import { cleanup, render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it } from 'vitest';
import { I18nProvider } from '../i18n';
// Aliased: a bare `it` import would shadow vitest's `it`.
import enMessages from '../i18n/en';
import itMessages from '../i18n/it';
import { ThemeContext } from '../theme/ThemeContext';
import type { EffectiveTheme } from '../theme/themePreference';
import { BrandMark } from './BrandMark';
import {
  BRAND_ASSETS,
  LOGO_CLEAR_SPACE_RATIO,
  MARK_CONTENT_RATIO,
  MIN_ICON_SIZE_PX,
  MIN_WORDMARK_WIDTH_PX,
  PRODUCT_NAME,
  SHELL_MARK_VISIBLE_PX,
  TV_PRODUCT_NAME,
  flatMarkUrl,
  markBoxForVisibleWidth,
  wordmarkAsset,
} from './brand';

const here = dirname(fileURLToPath(import.meta.url));
const FRONTEND = resolve(here, '../..');
const PUBLIC = resolve(FRONTEND, 'public');
const CSS = readFileSync(resolve(FRONTEND, 'src/styles.css'), 'utf8');
const INDEX_HTML = readFileSync(resolve(FRONTEND, 'index.html'), 'utf8');

// The canonical package is the source of truth for every shipped asset.
interface PackagedAsset { path: string; sha256: string; runtimeReady: boolean; width: number; height: number }
const PACKAGE_MANIFEST = JSON.parse(
  readFileSync(resolve(FRONTEND, '../assets/brand/nubarca/brand-manifest.json'), 'utf8'),
) as { assets: PackagedAsset[]; counts: Record<string, number> };

// The former product name. Written split so this file does not itself trip the
// repository-wide brand-cleanliness check it exists to complement.
const OLD_NAME = ['Nano', 'Cloud'].join('');

afterEach(cleanup);

describe('product name', () => {
  it('is NubArca, with a capital A', () => {
    expect(PRODUCT_NAME).toBe('NubArca');
    expect(TV_PRODUCT_NAME).toBe('NubArca TV');
    // The forbidden spellings.
    for (const wrong of ['Nubarca', 'NUBARCA', 'nubarca', 'Nub Arca']) {
      expect(PRODUCT_NAME).not.toBe(wrong);
    }
  });

});

describe('locale resources', () => {
  const locales = { en: enMessages, it: itMessages } as const;

  it('names the product identically in every locale — brand names are not translated', () => {
    for (const [tag, messages] of Object.entries(locales)) {
      expect(messages['app.name'], `${tag} app.name`).toBe(PRODUCT_NAME);
      expect(messages['tv.title'], `${tag} tv.title`).toBe(TV_PRODUCT_NAME);
    }
  });

  it('leaves the former name in no message of any locale, fallback included', () => {
    for (const [tag, messages] of Object.entries(locales)) {
      for (const [key, value] of Object.entries(messages)) {
        if (typeof value !== 'string') continue;
        expect(value.toLowerCase(), `${tag} → ${key}`).not.toContain(OLD_NAME.toLowerCase());
      }
    }
  });

  it('keeps the FALLBACK locale brand-clean, since an untranslated key renders it', () => {
    // English is Partial over the Italian keys: anything it omits renders the
    // Italian string. Italian is therefore the locale a stale brand would leak
    // through, so it is checked explicitly rather than only as one of many.
    const italianOnly = Object.keys(itMessages).filter((key) => !(key in enMessages));
    for (const key of italianOnly) {
      const value = (itMessages as Record<string, string>)[key];
      expect(value.toLowerCase(), `fallback → ${key}`).not.toContain(OLD_NAME.toLowerCase());
    }
    // And the brand keys themselves are defined in both, never left to fall back.
    for (const key of ['app.name', 'tv.title'] as const) {
      expect(enMessages[key], `en must define ${key}`).toBeDefined();
      expect(itMessages[key], `it must define ${key}`).toBeDefined();
    }
  });
});

describe('document head', () => {
  it('titles the document NubArca', () => {
    expect(INDEX_HTML).toContain('<title>NubArca</title>');
    expect(INDEX_HTML).not.toContain(OLD_NAME);
  });

  it('carries a description, an application name and the brand theme colour', () => {
    expect(INDEX_HTML).toMatch(/<meta\s+name="description"/);
    expect(INDEX_HTML).toContain('NubArca — your files, your hardware, your private cloud.');
    expect(INDEX_HTML).toContain('name="application-name" content="NubArca"');
    // Midnight Navy, the brand's principal dark background.
    expect(INDEX_HTML).toContain('content="#0a0f1a"');
  });

  it('links a favicon, an apple touch icon and the manifest', () => {
    expect(INDEX_HTML).toContain('href="/brand/favicon.ico"');
    expect(INDEX_HTML).toContain('rel="apple-touch-icon"');
    expect(INDEX_HTML).toContain('href="/manifest.webmanifest"');
  });
});

describe('PWA manifest', () => {
  const manifest = JSON.parse(
    readFileSync(resolve(PUBLIC, 'manifest.webmanifest'), 'utf8'),
  ) as {
    name: string; short_name: string; description: string;
    background_color: string; theme_color: string;
    icons: Array<{ src: string; sizes: string; purpose: string }>;
  };

  it('installs as NubArca', () => {
    expect(manifest.name).toBe('NubArca');
    expect(manifest.short_name).toBe('NubArca');
    expect(JSON.stringify(manifest)).not.toContain(OLD_NAME);
  });

  it('uses the brand background so the splash is Midnight Navy', () => {
    expect(manifest.background_color).toBe('#0a0f1a');
    expect(manifest.theme_color).toBe('#0a0f1a');
  });

  it('declares 192, 512 and a maskable icon, all present on disk', () => {
    const sizes = manifest.icons.map((i) => i.sizes);
    expect(sizes).toContain('192x192');
    expect(sizes).toContain('512x512');
    expect(manifest.icons.some((i) => i.purpose === 'maskable')).toBe(true);
    for (const icon of manifest.icons) {
      expect(existsSync(resolve(PUBLIC, icon.src.replace(/^\//, ''))), icon.src).toBe(true);
    }
  });
});

describe('brand artwork', () => {
  it('ships every asset the code references', () => {
    for (const [role, path] of Object.entries(BRAND_ASSETS)) {
      expect(existsSync(resolve(PUBLIC, path.replace(/^\//, ''))), `${role} → ${path}`).toBe(true);
    }
  });

  // The single most important property of this integration: every served file is
  // a byte-exact copy of an approved asset. If a build step ever resized,
  // recoloured or regenerated one, its hash would stop matching the package.
  it('serves only byte-exact copies of approved canonical assets', () => {
    const canonical = new Map<string, string>();
    for (const a of PACKAGE_MANIFEST.assets) {
      canonical.set(basename(a.path), a.sha256);
    }
    const served = readdirSync(resolve(PUBLIC, 'brand'));
    expect(served.length).toBeGreaterThan(20);
    for (const file of served) {
      const expected = canonical.get(file);
      expect(expected, `${file} is not in the canonical package`).toBeDefined();
      const actual = createHash('sha256')
        .update(readFileSync(resolve(PUBLIC, 'brand', file)))
        .digest('hex');
      expect(actual, `${file} differs from the approved binary`).toBe(expected);
    }
  });

  it('serves nothing the manifest marks as not runtime-ready', () => {
    const byName = new Map(PACKAGE_MANIFEST.assets.map((a) => [basename(a.path), a]));
    for (const file of readdirSync(resolve(PUBLIC, 'brand'))) {
      expect(byName.get(file)?.runtimeReady, `${file} is not runtime-ready`).toBe(true);
    }
  });

  it('keeps the guideline boards out of the served directory', () => {
    // The reference boards are documentation, never runtime UI.
    const served = readdirSync(resolve(PUBLIC, 'brand'));
    for (const file of served) {
      expect(file.toLowerCase()).not.toMatch(/reference|board|poster|guide|preview/);
    }
    // …and none of them is even catalogued as runtime-ready.
    for (const a of PACKAGE_MANIFEST.assets) {
      if (a.path.startsWith('reference/')) expect(a.runtimeReady).toBe(false);
    }
  });

  it('leaves no old-brand artwork behind in the served directory', () => {
    for (const file of readdirSync(resolve(PUBLIC, 'brand'))) {
      expect(file.toLowerCase()).not.toContain(OLD_NAME.toLowerCase());
    }
  });

  it('ships the whole approved favicon family', () => {
    for (const f of ['favicon.ico', 'favicon-16.png', 'favicon-24.png', 'favicon-32.png', 'favicon-48.png']) {
      expect(existsSync(resolve(PUBLIC, 'brand', f)), f).toBe(true);
    }
  });

  it('ships the font licences alongside the bundled faces', () => {
    expect(existsSync(resolve(PUBLIC, 'fonts/space-grotesk-OFL.txt'))).toBe(true);
    expect(existsSync(resolve(PUBLIC, 'fonts/exo-2-OFL.txt'))).toBe(true);
    for (const name of ['space-grotesk-OFL.txt', 'exo-2-OFL.txt']) {
      expect(readFileSync(resolve(PUBLIC, 'fonts', name), 'utf8'))
        .toContain('SIL Open Font License');
    }
  });
});

// The TV app copies from the same canonical package.
describe('TV artwork', () => {
  const TV = resolve(FRONTEND, '../tv/assets/brand');
  const CONFIG = readFileSync(resolve(FRONTEND, '../tv/app.config.js'), 'utf8');

  it('serves only byte-exact copies of approved canonical assets', () => {
    const canonical = new Map(PACKAGE_MANIFEST.assets.map((a) => [basename(a.path), a]));
    for (const file of readdirSync(TV)) {
      const record = canonical.get(file);
      expect(record, `${file} is not in the canonical package`).toBeDefined();
      expect(record!.runtimeReady, `${file} is not runtime-ready`).toBe(true);
      const actual = createHash('sha256').update(readFileSync(resolve(TV, file))).digest('hex');
      expect(actual, `${file} differs from the approved binary`).toBe(record!.sha256);
    }
  });

  it('points the Expo config at the approved assets that exist', () => {
    const referenced = [...CONFIG.matchAll(/'\.\/assets\/brand\/([^']+)'/g)].map((m) => m[1]);
    expect(referenced.length).toBeGreaterThanOrEqual(3);
    for (const f of referenced) {
      expect(existsSync(resolve(TV, f)), `app.config.js references a missing ${f}`).toBe(true);
    }
  });

  it('uses the purpose-built banner rather than a stretched 3:2 lockup', () => {
    expect(CONFIG).toContain('nubarca-android-tv-banner-320x180.png');
    const banner = PACKAGE_MANIFEST.assets
      .find((a) => a.path.endsWith('nubarca-android-tv-banner-320x180.png'))!;
    expect([banner.width, banner.height]).toEqual([320, 180]);
  });

  it('uses a square launcher icon, never a wide lockup', () => {
    for (const m of [...CONFIG.matchAll(/icon: '\.\/assets\/brand\/([^']+)'/g)]) {
      const rec = PACKAGE_MANIFEST.assets.find((a) => a.path.endsWith(m[1]))!;
      expect(rec.width, `${m[1]} is not square`).toBe(rec.height);
    }
  });
});

describe('brand mark', () => {
  function renderMark(props: Parameters<typeof BrandMark>[0] = {}, theme: EffectiveTheme = 'dark') {
    return render(
      <ThemeContext.Provider value={{ preference: theme, effective: theme, setPreference: () => {} }}>
        <I18nProvider>
          <BrandMark {...props} />
        </I18nProvider>
      </ThemeContext.Provider>,
    );
  }
  const src = () => screen.getByTestId('brand-mark').querySelector('img')!.getAttribute('src')!;

  it('announces the product name exactly once', () => {
    renderMark();
    const mark = screen.getByTestId('brand-mark');
    expect(mark).toHaveAttribute('aria-label', PRODUCT_NAME);
    // The artwork is decorative: the label already carries the name.
    expect(mark.querySelector('img')).toHaveAttribute('aria-hidden', 'true');
    expect(mark.textContent).toBe('');
  });

  // The rule this component exists to enforce.
  it('uses the FLAT mark in small UI contexts, never the launcher icon', () => {
    renderMark({ size: 26 });
    expect(src()).toContain('nubarca-mark-flat-on-');
    for (const launcher of ['pwa-', 'app-icon', 'maskable', 'apple-touch', 'favicon']) {
      expect(src()).not.toContain(launcher);
    }
  });

  it('picks the on-dark or on-light mark from the RESOLVED theme', () => {
    renderMark({ size: 26 }, 'dark');
    expect(src()).toContain('-on-dark-');
    cleanup();
    renderMark({ size: 26 }, 'light');
    expect(src()).toContain('-on-light-');
  });

  it('never renders the mark below the 24px brand minimum', () => {
    renderMark({ size: 8 });
    const img = screen.getByTestId('brand-mark').querySelector('img') as HTMLImageElement;
    expect(Number(img.getAttribute('width'))).toBeGreaterThanOrEqual(MIN_ICON_SIZE_PX);
  });

  it('serves a mark at least 2x the rendered box so it stays crisp', () => {
    renderMark({ size: 26 });
    expect(src()).toContain('-64.png');
    cleanup();
    // 16 is below the 24px brand minimum, so the component clamps to 24 and then
    // asks for 2x — the 48px file, not the 32px one a naive 16x2 would pick.
    renderMark({ size: 16 });
    expect(src()).toContain('-48.png');
    // The picker itself, unclamped, still resolves the small sizes.
    expect(flatMarkUrl('dark', 16)).toContain('-32.png');
  });

  it('uses the approved wordmark lockup for prominent placements', () => {
    renderMark({ variant: 'wordmark', size: 200 }, 'dark');
    expect(src()).toContain('nubarca-wordmark-on-dark-');
    cleanup();
    renderMark({ variant: 'wordmark', size: 200 }, 'light');
    expect(src()).toBe('/brand/nubarca-wordmark-on-light.png');
  });

  it('never renders the wordmark below its 120px minimum, in either theme', () => {
    for (const theme of ['dark', 'light'] as const) {
      cleanup();
      renderMark({ variant: 'wordmark', size: 40 }, theme);
      const img = screen.getByTestId('brand-mark').querySelector('img') as HTMLImageElement;
      const element = Number(img.getAttribute('width'));
      // The light file pads the lockup inside a larger canvas, so the ELEMENT is
      // wider than the visible lockup. What must clear the minimum is the lockup.
      const ratio = theme === 'light' ? 0.7724 : 0.9833;
      expect(Math.round(element * ratio)).toBeGreaterThanOrEqual(MIN_WORDMARK_WIDTH_PX);
    }
  });

  it('reserves the brand clear space around the lockup', () => {
    renderMark({ size: 40 });
    const pad = Number.parseFloat(screen.getByTestId('brand-mark').style.padding);
    expect(pad).toBeCloseTo(40 * LOGO_CLEAR_SPACE_RATIO, 0);
  });

  it('drops its own padding where the placement supplies the clear space', () => {
    renderMark({ size: 41, clearSpace: false });
    expect(screen.getByTestId('brand-mark').style.padding).toBe('');
  });

  it('publishes the box size so a placement can step it down in CSS', () => {
    renderMark({ size: 41 });
    expect(screen.getByTestId('brand-mark').style.getPropertyValue('--brand-mark-size'))
      .toBe('41px');
  });

  // The defect this slice fixes: the shell mark was a 26px box holding ~13.5px
  // of artwork. Both halves moved — a bigger box AND artwork that fills it.
  it('renders a materially larger VISIBLE mark in the shell than before', () => {
    const box = markBoxForVisibleWidth(SHELL_MARK_VISIBLE_PX.desktop);
    const visible = box * MARK_CONTENT_RATIO.width;
    expect(visible).toBeGreaterThanOrEqual(35);
    // The pre-UX-02 shell: a 26px box of artwork that occupied 51.6% of it.
    expect(visible).toBeGreaterThan(26 * 0.516 * 2);
  });

  it('never points at a reference board or a source master', () => {
    for (const props of [{}, { variant: 'wordmark' as const }]) {
      cleanup();
      renderMark(props);
      expect(src()).not.toMatch(/reference|board|poster|guide|master/);
      expect(src().startsWith('/brand/')).toBe(true);
    }
  });
});

describe('asset pickers', () => {
  it('map every shipped size to a file that exists', () => {
    for (const theme of ['dark', 'light'] as const) {
      for (const px of [12, 16, 24, 26, 32, 48, 64, 128]) {
        const url = flatMarkUrl(theme, px);
        expect(existsSync(resolve(PUBLIC, url.replace(/^\//, ''))), url).toBe(true);
      }
      for (const w of [120, 200, 240, 480, 700]) {
        const { src: url } = wordmarkAsset(theme, w);
        expect(existsSync(resolve(PUBLIC, url.replace(/^\//, ''))), url).toBe(true);
      }
    }
  });

  it('never serves a light-surface asset to a dark surface', () => {
    expect(flatMarkUrl('dark', 24)).not.toContain('on-light');
    expect(flatMarkUrl('light', 24)).not.toContain('on-dark');
    expect(wordmarkAsset('dark', 200).src).not.toContain('on-light');
    expect(wordmarkAsset('light', 200).src).not.toContain('on-dark');
  });
});

describe('brand geometry and palette tokens', () => {
  function ruleBody(selector: string): string {
    const pattern = new RegExp(`^${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{`, 'm');
    const match = pattern.exec(CSS);
    if (match === null) throw new Error(`No rule for "${selector}".`);
    const start = CSS.indexOf('{', match.index);
    return CSS.slice(start + 1, CSS.indexOf('}', start));
  }

  it('defines the official palette verbatim', () => {
    const root = ruleBody(':root');
    const official: Array<[string, string]> = [
      ['--brand-midnight-navy', '#0a0f1a'],
      ['--brand-deep-blue', '#0f1e3a'],
      ['--brand-electric-blue', '#1565ff'],
      ['--brand-cyan-glow', '#00d4ff'],
      ['--brand-soft-violet', '#9a6cff'],
      ['--brand-cloud-white', '#f5f7fb'],
    ];
    for (const [token, hex] of official) {
      expect(root, `${token} must be exactly ${hex}`).toContain(`${token}: ${hex}`);
    }
  });

  it('maps the dark surfaces onto the brand backgrounds', () => {
    const root = ruleBody(':root');
    expect(root).toContain('--surface-canvas: var(--brand-midnight-navy)');
    expect(root).toContain('--surface-raised: var(--brand-deep-blue)');
    expect(root).toContain('--text-primary: var(--brand-cloud-white)');
  });

  it('keeps Soft Violet off the primary action colour', () => {
    const root = ruleBody(':root');
    expect(root).toContain('--accent-secondary: var(--brand-soft-violet)');
    expect(root).not.toMatch(/--accent:\s*var\(--brand-soft-violet\)/);
    expect(ruleBody(":root[data-theme='light']")).not.toContain('9a6cff');
  });

  it('states the brand geometry as tokens', () => {
    const root = ruleBody(':root');
    expect(root).toContain('--space-unit: 8px');
    expect(root).toContain('--radius-card: 16px');
    expect(root).toContain('--radius-button: 12px');
  });

  it('sets both brand faces with a usable fallback stack', () => {
    const root = ruleBody(':root');
    expect(root).toMatch(/--font-heading:\s*'Space Grotesk',/);
    expect(root).toMatch(/--font-ui:\s*'Exo 2',/);
    // A font-load failure must still leave readable text.
    expect(root).toMatch(/--font-heading:[^;]*sans-serif/);
    expect(root).toMatch(/--font-ui:[^;]*sans-serif/);
    // Monospace is deliberately unchanged — logs and hashes need fixed pitch.
    expect(root).toContain('--font-mono: ui-monospace');
  });

  it('gives controls the UI face explicitly', () => {
    expect(CSS).toMatch(/button,\s*\n\s*input,\s*\n\s*select,\s*\n\s*textarea,\s*\n\s*optgroup\s*\{\s*\n\s*font-family: var\(--font-ui\);/);
  });

  it('respects the minimum icon and wordmark sizes from the guidelines', () => {
    expect(MIN_ICON_SIZE_PX).toBe(24);
    expect(MIN_WORDMARK_WIDTH_PX).toBe(120);
    expect(ruleBody('.brand-mark__icon')).toContain(`min-width: ${MIN_ICON_SIZE_PX}px`);
  });

  // UX-02: the mark box is published as a custom property so ONE topbar can
  // serve a desktop and a mobile size without a second component instance.
  it('sizes the mark from the custom property the component publishes', () => {
    const icon = ruleBody('.brand-mark__icon');
    expect(icon).toContain('width: var(--brand-mark-size)');
    expect(icon).toContain('height: var(--brand-mark-size)');
  });

  it('steps the shell mark down on mobile without a second instance', () => {
    const mobileBox = markBoxForVisibleWidth(SHELL_MARK_VISIBLE_PX.mobile);
    // The override targets the IMAGE. --brand-mark-size is written as an inline
    // style by the component, and an inline declaration beats a stylesheet
    // rule — a media query on the custom property is silently ignored, which
    // is exactly what the browser matrix caught.
    expect(CSS).toMatch(
      new RegExp(`\\.app-brand-lockup \\.brand-mark__icon \\{\\s*width: ${mobileBox}px;`),
    );
  });

  it('supplies the shell clear space from the placement, not from mark padding', () => {
    // The requirement is 25% of the RENDERED LOGO HEIGHT. Reserving it as
    // padding inside a fixed box is what made the old mark look small.
    const box = markBoxForVisibleWidth(SHELL_MARK_VISIBLE_PX.desktop);
    const requiredPx = box * MARK_CONTENT_RATIO.height * LOGO_CLEAR_SPACE_RATIO;
    const lockup = ruleBody('.app-brand-lockup');
    const declared = Number.parseFloat(/padding-inline:\s*([\d.]+)px/.exec(lockup)![1]);
    expect(declared).toBeGreaterThanOrEqual(requiredPx);
  });

  it('loads no font from a third-party origin', () => {
    // Everything is bundled from node_modules by Vite and served same-origin.
    expect(CSS).not.toMatch(/@import\s+url\(['"]?https?:/);
    expect(CSS).not.toContain('fonts.googleapis.com');
    expect(CSS).not.toContain('fonts.gstatic.com');
    expect(INDEX_HTML).not.toContain('fonts.googleapis.com');
    expect(INDEX_HTML).not.toContain('fonts.gstatic.com');
  });
});

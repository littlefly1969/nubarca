import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// Stylesheet-level regressions for the theming contract.
//
// jsdom does not apply the real stylesheet, so a rendered-DOM test cannot prove
// a colour is readable. What CAN be pinned mechanically is the structure the
// readability depends on: both palettes define every semantic token, the app
// palette is not driven by prefers-color-scheme (which would let the OS
// override an explicit Light choice), and the controls this slice fixed do not
// hard-code colours again.

const here = dirname(fileURLToPath(import.meta.url));
const CSS = readFileSync(resolve(here, '../styles.css'), 'utf8');

// Extract the body of the first rule whose selector matches EXACTLY.
// The selector must start a line, so looking up `select` does not accidentally
// match `.language-switcher select` or `.ws-sort select`.
function ruleBody(selector: string): string {
  const pattern = new RegExp(
    `^${selector.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}\\s*\\{`,
    'm',
  );
  const match = pattern.exec(CSS);
  if (match === null) throw new Error(`No rule found for selector "${selector}".`);
  const start = CSS.indexOf('{', match.index);
  const end = CSS.indexOf('}', start);
  return CSS.slice(start + 1, end);
}

const REQUIRED_TOKENS = [
  '--surface-canvas',
  '--surface-raised',
  '--surface-overlay',
  '--surface-subtle',
  '--border-default',
  '--border-strong',
  '--text-primary',
  '--text-secondary',
  '--text-muted',
  '--accent',
  '--accent-hover',
  '--focus-ring',
  '--danger',
  '--success',
  '--shadow-sm',
  '--shadow-md',
  '--shadow-lg',
];

describe('design tokens', () => {
  it('defines every semantic token in the default (dark) palette', () => {
    const dark = ruleBody(':root');
    for (const token of REQUIRED_TOKENS) {
      expect(dark, `:root is missing ${token}`).toContain(`${token}:`);
    }
  });

  it('overrides every colour token in the light palette', () => {
    const light = ruleBody(":root[data-theme='light']");
    // Shadows and colours both flip; every token must have a light value, or a
    // dark value would leak into the light theme.
    for (const token of REQUIRED_TOKENS) {
      expect(light, `light palette is missing ${token}`).toContain(`${token}:`);
    }
  });

  it('declares an explicit color-scheme per theme so native controls follow', () => {
    expect(ruleBody(':root')).toContain('color-scheme: dark');
    expect(ruleBody(":root[data-theme='light']")).toContain('color-scheme: light');
  });

  it('keeps the legacy aliases pointing at semantic tokens', () => {
    const dark = ruleBody(':root');
    // The rest of the 6k-line stylesheet still uses these names; if they stop
    // resolving to tokens, half the app silently leaves the theme.
    expect(dark).toContain('--bg: var(--surface-canvas)');
    expect(dark).toContain('--fg: var(--text-primary)');
    expect(dark).toContain('--muted: var(--text-muted)');
    expect(dark).toContain('--border: var(--border-default)');
    expect(dark).toContain('--error: var(--danger)');
  });

  it('does not drive the app palette from prefers-color-scheme', () => {
    // The OS signal is consumed by the `system` preference in JS. A media query
    // here would let a dark-mode OS override an explicit Light choice.
    const paletteMediaQuery = /@media\s*\(prefers-color-scheme[^)]*\)\s*\{\s*:root/;
    expect(paletteMediaQuery.test(CSS)).toBe(false);
  });

  it('respects the reduced-motion preference', () => {
    expect(CSS).toContain('prefers-reduced-motion: reduce');
  });
});

describe('native select readability', () => {
  it('gives every select explicit foreground and background tokens', () => {
    const body = ruleBody('select');
    expect(body).toContain('color: var(--text-primary)');
    expect(body).toContain('background-color: var(--surface-raised)');
    expect(body).toContain('color-scheme: inherit');
  });

  it('keeps a disabled select legible rather than a washed-out ghost', () => {
    const body = ruleBody('select:disabled');
    expect(body).toContain('color: var(--text-secondary)');
    expect(body).toContain('opacity: 1');
  });

  it('styles the option list too, where the browser allows it', () => {
    const body = ruleBody('select option');
    expect(body).toContain('color: var(--text-primary)');
    expect(body).toContain('background-color: var(--surface-raised)');
  });

  it('never hard-codes a background on the language select again', () => {
    // The original bug: `background: #171a21` with `color: inherit`, which in a
    // light theme rendered dark text on a dark box — the selected language was
    // invisible.
    const body = ruleBody('.language-switcher select');
    expect(body).not.toMatch(/background[^;]*#[0-9a-f]{3,8}/i);
    expect(body).not.toContain('color: inherit');
  });
});

// Real-browser verification measured these two at 3.6:1 and 3.74:1 — both below
// AA for normal text. The cause in each case was the same: --accent is the
// legibility TINT of Electric Blue, meant for text and borders, and printing
// something on top of it (or printing it on a tinted background) loses the
// headroom the tint was created to provide.
describe('accent fills that carry text', () => {
  it('gives the sign-in button the brand fill, not the accent tint', () => {
    const body = ruleBody(".login-card button[type='submit']");
    // white on --accent (#3D82FF) is 3.6:1; on --accent-strong (#1565FF) 4.84:1.
    expect(body).toContain('background: var(--accent-strong)');
    expect(body).toContain('color: var(--accent-contrast)');
    expect(body).not.toMatch(/background:\s*var\(--accent\)/);
    expect(body).not.toMatch(/color:\s*#fff/i);
  });

  it('gives the sign-in button a visible focus ring of its own', () => {
    // With no rule it inherited the UA's 1px black outline — invisible on dark.
    const body = ruleBody(".login-card button[type='submit']:focus-visible");
    expect(body).toContain('outline: 2px solid var(--focus-ring)');
  });

  it('does not print the accent tint on the accent-tinted count pill', () => {
    const body = ruleBody('.media-kind-tab.is-active .media-kind-tab-count');
    expect(body).toContain('color: var(--text-primary)');
    expect(body).not.toMatch(/color:\s*var\(--accent\)/);
  });
});

describe('focus visibility', () => {
  it('uses the focus-ring token for the controls this slice introduced', () => {
    for (const selector of [
      '.app-nav-link:focus-visible',
      '.icon-button:focus-visible',
      '.user-menu__trigger:focus-visible',
      '.cloud-tool-tab:focus-visible',
      '.media-kind-tab:focus-visible',
      '.media-scope-tab:focus-visible',
      '.ws-tool-button:focus-visible',
      '.metadata-action:focus-visible',
      'select:focus-visible',
    ]) {
      expect(ruleBody(selector), `${selector} has no visible focus ring`)
        .toContain('outline: 2px solid var(--focus-ring)');
    }
  });
});

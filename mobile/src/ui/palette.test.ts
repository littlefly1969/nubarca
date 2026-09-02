import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';
import { darkPalette, identity, lightPalette, media, palettes, type Palette } from './palette.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const MOBILE_ROOT = resolve(HERE, '..', '..');
const BRAND_DOC = readFileSync(resolve(MOBILE_ROOT, '..', 'docs', 'brand.md'), 'utf8');

/** The hexes the brand document publishes, read out of its own table. */
function brandHex(name: string): string {
  const row = new RegExp(`\\| \`--brand-${name}\` \\|[^|]*\\| \`(#[0-9A-Fa-f]{6})\` \\|`).exec(BRAND_DOC);
  assert.ok(row, `docs/brand.md no longer publishes --brand-${name}`);
  return row[1].toUpperCase();
}

test('the palettes are built from the approved brand hexes, not from near misses', () => {
  // Reading the document rather than restating it: this is what makes the
  // mobile palette a CONSUMER of the brand instead of a second opinion on it.
  const midnightNavy = brandHex('midnight-navy');
  const cloudWhite = brandHex('cloud-white');
  const deepBlue = brandHex('deep-blue');

  assert.equal(darkPalette.canvas, midnightNavy);
  assert.equal(darkPalette.surface, deepBlue);
  assert.equal(darkPalette.textPrimary, cloudWhite);

  assert.equal(lightPalette.canvas, cloudWhite);
  assert.equal(lightPalette.textPrimary, midnightNavy);

  assert.equal(media.background, midnightNavy);
  assert.equal(media.text, cloudWhite);
});

test('the accent is the legibility tint the brand document mandates, per theme', () => {
  // Electric Blue itself fails WCAG AA as text on either canvas. The document
  // names the two tints that do not; using the raw brand hex here would be the
  // exact regression it was written to prevent.
  const tints = [...BRAND_DOC.matchAll(/- (dark|light) theme `--accent: (#[0-9A-Fa-f]{6})`/g)];
  assert.equal(tints.length, 2, 'docs/brand.md no longer names both accent tints');
  const byTheme = Object.fromEntries(tints.map((m) => [m[1], m[2].toUpperCase()]));

  assert.equal(darkPalette.accent, byTheme.dark);
  assert.equal(lightPalette.accent, byTheme.light);
  assert.notEqual(darkPalette.accent, brandHex('electric-blue'));
  assert.notEqual(lightPalette.accent, brandHex('electric-blue'));
});

test('both themes answer every role', () => {
  // A role missing from one palette is not a type error if the other supplies
  // it through a shared literal; it is a colour that silently stops existing.
  const roles = Object.keys(lightPalette) as (keyof Palette)[];
  for (const theme of ['dark', 'light'] as const) {
    for (const role of roles) {
      const value = palettes[theme][role];
      assert.ok(
        typeof value === 'string' && value.length > 0,
        `${theme} palette has no ${role}`,
      );
    }
  }
});

test('the two themes actually differ where a reader would notice', () => {
  for (const role of ['canvas', 'surface', 'textPrimary', 'textSecondary', 'accent'] as const) {
    assert.notEqual(
      darkPalette[role],
      lightPalette[role],
      `${role} is the same in both themes, so one of them is unreadable`,
    );
  }
});

function sources(dir: string, out: string[] = []): string[] {
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules') continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) sources(full, out);
    else if (/\.tsx?$/.test(entry) && !/\.test\.tsx?$/.test(entry)) out.push(full);
  }
  return out;
}

test('no screen or component states a colour of its own', () => {
  // docs/brand.md: "No component may introduce a brand colour of its own."
  // Before the dark theme there were about sixty hardcoded literals, and every
  // one of them would have stayed light-mode forever — silently, because a hex
  // renders perfectly whatever the theme is. This is the assertion that keeps
  // the next one from being added.
  const root = MOBILE_ROOT;
  const offenders: string[] = [];
  for (const file of [...sources(join(root, 'src')), ...sources(join(root, 'app'))]) {
    if (file.endsWith(join('src', 'ui', 'palette.ts'))) continue;
    const text = code(readFileSync(file, 'utf8'));
    const hits = text.match(/#[0-9A-Fa-f]{3,8}\b|\brgba?\(/g);
    if (hits) offenders.push(`${file.slice(root.length + 1)}: ${[...new Set(hits)].join(', ')}`);
  }
  assert.deepEqual(offenders, [], `colour literals outside palette.ts:\n${offenders.join('\n')}`);
});

// WCAG relative luminance, so the palette's own promises are checked rather
// than asserted. The brand document is explicit about contrast; a palette that
// only claims to respect it is a palette nobody has measured.
function contrast(a: string, b: string): number {
  const channel = (hex: string, i: number): number => {
    const v = parseInt(hex.slice(1 + i * 2, 3 + i * 2), 16) / 255;
    return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
  };
  const lum = (hex: string): number =>
    0.2126 * channel(hex, 0) + 0.7152 * channel(hex, 1) + 0.0722 * channel(hex, 2);
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

test('every text role clears WCAG AA on the surface it is read against', () => {
  const AA = 4.5;
  for (const theme of ['dark', 'light'] as const) {
    const p = palettes[theme];
    const pairs: [string, string, string][] = [
      ['textPrimary on canvas', p.textPrimary, p.canvas],
      ['textPrimary on surface', p.textPrimary, p.surface],
      ['textSecondary on canvas', p.textSecondary, p.canvas],
      ['textTertiary on canvas', p.textTertiary, p.canvas],
      ['textTertiary on surfaceMuted', p.textTertiary, p.surfaceMuted],
      ['accent on canvas', p.accent, p.canvas],
      ['accent on surface', p.accent, p.surface],
      // The chip case: an accent label on its own accent wash.
      ['accent on accentSubtle', p.accent, p.accentSubtle],
      // The fill case, and the reason accentStrong exists at all: white on the
      // lighter accent TINT is 3.6:1, which docs/brand.md warns about by name.
      ['textOnAccent on accentStrong', p.textOnAccent, p.accentStrong],
      // The three status signals carry meaning, so they must be READABLE, not
      // merely present (BRAND-COLOR-SEMANTICS-01).
      ['signalConnected on canvas', p.signalConnected, p.canvas],
      ['signalIntelligence on canvas', p.signalIntelligence, p.canvas],
      ['signalSuccess on canvas', p.signalSuccess, p.canvas],
      ['danger on canvas', p.danger, p.canvas],
      ['danger on dangerSurface', p.danger, p.dangerSurface],
      ['warningText on warningSurface', p.warningText, p.warningSurface],
    ];
    for (const [what, fg, bg] of pairs) {
      const ratio = contrast(fg, bg);
      assert.ok(ratio >= AA, `${theme}: ${what} is ${ratio.toFixed(2)}:1, below AA`);
    }
  }
});

test('accent fills use accentStrong, never the text tint', () => {
  // Enforced on the SOURCE because the palette cannot see how it is used: the
  // mistake is invisible until somebody measures white on a button.
  const offenders: string[] = [];
  for (const file of [...sources(join(MOBILE_ROOT, 'src')), ...sources(join(MOBILE_ROOT, 'app'))]) {
    const text = code(readFileSync(file, 'utf8'));
    if (/backgroundColor: colors\.accent\b(?!S)/.test(text)) {
      offenders.push(file.slice(MOBILE_ROOT.length + 1));
    }
  }
  assert.deepEqual(offenders, [], `accent used as a fill in: ${offenders.join(', ')}`);
});

// --- Semantic parity with the design contract ------------------------------
//
// The mobile palette is an ADAPTER of design/tokens/semantic.*.json, not a
// second opinion about it. Comparing real imported values against the real
// contract files is what makes that true: a value edited on either side fails
// here, and neither can drift quietly toward the other.

/** design token path -> the Palette role that adapts it. */
const SEMANTIC_ROLES: [string, keyof Palette][] = [
  ['surface.canvas', 'canvas'],
  ['surface.raised', 'surface'],
  ['surface.overlay', 'surfaceOverlay'],
  ['surface.subtle', 'surfaceSubtle'],
  ['text.primary', 'textPrimary'],
  ['text.secondary', 'textSecondary'],
  ['text.muted', 'textTertiary'],
  ['text.onAccent', 'textOnAccent'],
  ['action.accentText', 'accent'],
  ['action.primaryFill', 'accentStrong'],
  ['action.subtle', 'accentSubtle'],
  ['signal.focus', 'signalFocus'],
  ['signal.connected', 'signalConnected'],
  ['signal.intelligence', 'signalIntelligence'],
  ['signal.danger', 'danger'],
  ['signal.success', 'signalSuccess'],
];

const IDENTITY_ROLES: [string, keyof typeof identity][] = [
  ['bootBackground', 'bootBackground'],
  ['bootForeground', 'bootForeground'],
  ['bootActivity', 'bootActivity'],
];

interface SemanticTokens {
  [group: string]: Record<string, string> | string | number;
}

function designTokens(theme: 'dark' | 'light'): SemanticTokens {
  return JSON.parse(
    readFileSync(resolve(MOBILE_ROOT, '..', 'design', 'tokens', `semantic.${theme}.json`), 'utf8'),
  ) as SemanticTokens;
}

function primitives(): Record<string, string> {
  const parsed = JSON.parse(
    readFileSync(resolve(MOBILE_ROOT, '..', 'design', 'tokens', 'brand.primitives.json'), 'utf8'),
  ) as { color: Record<string, string> };
  return parsed.color;
}

/** `{color.midnightNavy}` is a reference; anything else is already a value. */
function resolveToken(value: string, color: Record<string, string>): string {
  const reference = /^\{color\.(\w+)\}$/.exec(value);
  return reference === null ? value : color[reference[1]];
}

test('the mobile palette adapts the design semantic tokens exactly', () => {
  const color = primitives();
  for (const theme of ['dark', 'light'] as const) {
    const tokens = designTokens(theme);
    for (const [path, role] of SEMANTIC_ROLES) {
      const [group, key] = path.split('.');
      const raw = (tokens[group] as Record<string, string>)[key];
      assert.ok(raw !== undefined, `design semantic.${theme}.json has no ${path}`);
      assert.equal(
        palettes[theme][role].toUpperCase(),
        resolveToken(raw, color).toUpperCase(),
        `${theme}: ${role} disagrees with design token ${path}`,
      );
    }
  }
});

test('the identity roles are the same in both themes, and match the contract', () => {
  // Theme-independent by contract: a cold launch is Midnight Navy whichever
  // theme the user eventually gets.
  const color = primitives();
  for (const theme of ['dark', 'light'] as const) {
    const tokens = designTokens(theme);
    for (const [path, role] of IDENTITY_ROLES) {
      const raw = (tokens.identity as Record<string, string>)[path];
      assert.equal(
        identity[role].toUpperCase(),
        resolveToken(raw, color).toUpperCase(),
        `${theme}: identity.${role} disagrees with the contract`,
      );
    }
  }
});

test('there is no generic route to Soft Violet', () => {
  // BRAND-AI-01: Soft Violet identifies AI and inference. A general-purpose
  // `highlight` role guarantees it will be spent on decoration — it already had
  // been, on a duplicate COUNT, which a database produces without a model. Once
  // that happens the product has no colour left that means "inference".
  //
  // `signalIntelligence` remains, in the themed palette, where its name says
  // what it is for.
  assert.equal((media as Record<string, unknown>).highlight, undefined);
  const source = readFileSync(resolve(MOBILE_ROOT, 'src', 'ui', 'palette.ts'), 'utf8');
  const mediaBlock = source.slice(source.indexOf('export const media = {'));
  assert.doesNotMatch(code(mediaBlock), /154, 108, 255|softViolet/);
});

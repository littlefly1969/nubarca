import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';
import { darkPalette, lightPalette, media, palettes, type Palette } from './palette.ts';

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

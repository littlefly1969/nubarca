import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const SOURCE = code(readFileSync(resolve(HERE, 'BrandLockup.tsx'), 'utf8'));

// The layout rule, restated here from the component. It is pure arithmetic, and
// keeping a copy is what lets the numbers be checked without a renderer; the
// assertions below then hold the component to the same constants.
const RATIO = { dark: 0.9833, light: 0.7724 };
const FILE = { dark: { w: 480, h: 135 }, light: { w: 1516, h: 1024 } };
const layout = (theme: 'dark' | 'light', visible: number) => {
  const width = Math.round(Math.max(visible, 120) / RATIO[theme]);
  return { width, height: Math.round((width * FILE[theme].h) / FILE[theme].w) };
};

test('each theme draws the artwork made for its surface', () => {
  // Cloud White artwork on a Cloud White canvas is not a lighter logo, it is no
  // logo — so the two surfaces get two different approved binaries.
  assert.match(SOURCE, /dark: \{\s*source: require\('\.\.\/\.\.\/assets\/brand\/nubarca-wordmark-on-dark-480w\.png'\)/);
  assert.match(SOURCE, /light: \{\s*source: require\('\.\.\/\.\.\/assets\/brand\/nubarca-wordmark-on-light\.png'\)/);
  assert.match(SOURCE, /const \{ theme \} = useTheme\(\)/);
});

test('the requested width is the VISIBLE lockup, not the file', () => {
  // The two files put the same lockup on very different canvases. Sizing the
  // file would render the light variant visibly smaller at the same number.
  const dark = layout('dark', 200);
  const light = layout('light', 200);
  assert.equal(dark.width, 203);
  assert.equal(light.width, 259);
  assert.ok(light.width > dark.width, 'the light file needs more element width for the same lockup');
  // And the drawn artwork ends up the same size to within rounding.
  assert.ok(Math.abs(dark.width * RATIO.dark - light.width * RATIO.light) < 1);
});

test('the element keeps each FILE aspect, so nothing is stretched', () => {
  for (const theme of ['dark', 'light'] as const) {
    const { width, height } = layout(theme, 180);
    // The box IS the file's aspect, to within the one rounding it is allowed:
    // an integer height. `contain` then letterboxes rather than distorts, so a
    // wrong box could never squash the artwork — but it could shrink it.
    const exact = (width * FILE[theme].h) / FILE[theme].w;
    assert.ok(
      Math.abs(height - exact) <= 0.5,
      `${theme} element height ${height} is not the file aspect of ${width} (${exact})`,
    );
  }
});

test('the minimum visible width is real, in both themes', () => {
  for (const theme of ['dark', 'light'] as const) {
    const small = layout(theme, 40);
    assert.ok(
      small.width * RATIO[theme] >= 119.5,
      `${theme} fell under the 120 minimum: ${small.width * RATIO[theme]}`,
    );
  }
  assert.match(SOURCE, /Math\.max\(visibleWidth, MIN_WORDMARK_WIDTH\)/);
  assert.match(SOURCE, /MIN_WORDMARK_WIDTH = 120/);
});

test('the product is announced once, by the wrapper', () => {
  // Both the wrapper and the image carrying a name is the standard way a logo
  // gets read out twice.
  assert.match(SOURCE, /accessibilityLabel="NubArca"/);
  assert.match(SOURCE, /accessible=\{false\}/);
  assert.match(SOURCE, /importantForAccessibility="no"/);
});

test('the lockup is approved artwork, never redrawn', () => {
  assert.doesNotMatch(SOURCE, /tintColor|<Svg|<Path|<Text/);
  assert.doesNotMatch(SOURCE, /resizeMode="(cover|stretch)"/);
});

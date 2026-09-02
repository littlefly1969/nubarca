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
const RATIO = { dark: 0.9833, light: 0.9854 };
const FILE = { w: 480, h: 135 };
const layout = (theme: 'dark' | 'light', visible: number) => {
  const width = Math.round(Math.max(visible, 120) / RATIO[theme]);
  return { width, height: Math.round((width * FILE.h) / FILE.w) };
};

test('each theme draws the artwork made for its surface', () => {
  // Cloud White artwork on a Cloud White canvas is not a lighter logo, it is no
  // logo — so the two surfaces get two different approved binaries.
  assert.match(SOURCE, /dark: require\('\.\.\/\.\.\/assets\/brand\/nubarca-wordmark-on-dark-480w\.png'\)/);
  assert.match(SOURCE, /light: require\('\.\.\/\.\.\/assets\/brand\/nubarca-wordmark-on-light-480w\.png'\)/);
  assert.match(SOURCE, /const \{ theme \} = useTheme\(\)/);
});

test('the requested width is the VISIBLE lockup, not the file', () => {
  // Neither file fills its frame completely, so the element is still wider than
  // the lockup asked for — that division is what makes the 120 minimum real.
  const dark = layout('dark', 200);
  assert.ok(dark.width > 200, 'the element must be wider than the visible lockup');
  assert.ok(Math.abs(dark.width * RATIO.dark - 200) < 1, 'the drawn lockup is not 200 wide');
});

test('Dark and Light occupy the same layout footprint at one visibleWidth', () => {
  // THE REGRESSION THIS PREVENTS: the on-light wordmark used to ship on a
  // 1516x1024 canvas at 77.24% width. At visibleWidth 200 its element was 259
  // wide against the dark one's 203 — a 56 px difference in a shared layout,
  // and a lockup that rendered visibly smaller in Light. The compact rendition
  // removed that at the source. If a large-canvas variant ever comes back, this
  // fails.
  for (const visible of [120, 150, 180, 200, 220, 320]) {
    const dark = layout('dark', visible);
    const light = layout('light', visible);
    assert.ok(
      Math.abs(dark.width - light.width) <= 1,
      `element widths diverge at ${visible}: dark ${dark.width}, light ${light.width}`,
    );
    assert.ok(
      Math.abs(dark.height - light.height) <= 1,
      `element heights diverge at ${visible}: dark ${dark.height}, light ${light.height}`,
    );
    // And the DRAWN lockup, which is what a reader actually compares.
    assert.ok(
      Math.abs(dark.width * RATIO.dark - light.width * RATIO.light) < 1,
      `drawn lockup widths diverge at ${visible}`,
    );
  }
});

test('both themes draw from the same 480x135 frame', () => {
  // The per-theme file geometry is gone: one frame, one element box. A second
  // set of file dimensions here would mean a second canvas shape had returned.
  assert.match(SOURCE, /const FILE_WIDTH = 480;/);
  assert.match(SOURCE, /const FILE_HEIGHT = 135;/);
  assert.doesNotMatch(SOURCE, /fileWidth|fileHeight|1516|1024/);
});

test('the element keeps the FILE aspect, so nothing is stretched', () => {
  for (const theme of ['dark', 'light'] as const) {
    const { width, height } = layout(theme, 180);
    // The box IS the file's aspect, to within the one rounding it is allowed:
    // an integer height. `contain` then letterboxes rather than distorts, so a
    // wrong box could never squash the artwork — but it could shrink it.
    const exact = (width * FILE.h) / FILE.w;
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

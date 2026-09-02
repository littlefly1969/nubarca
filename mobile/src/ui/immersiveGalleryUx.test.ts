import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

/** Every primary gallery surface. The shell must not diverge between them. */
const GALLERIES: [string, string[]][] = [
  ['Photos', ['app', '(tabs)', 'photos.tsx']],
  ['Videos', ['app', '(tabs)', 'videos.tsx']],
  ['Albums', ['app', '(tabs)', 'albums.tsx']],
  ['owned album', ['app', 'album', '[id].tsx']],
  ['shared album', ['app', 'shared-album', '[id].tsx']],
];

test('every gallery surface runs the same shell', () => {
  // The slice is explicit that Photos, Videos and Albums must not diverge, and
  // that album detail gets no interaction model of its own. The way to
  // guarantee that is for all five to run the same code, not to look similar.
  for (const [name, path] of GALLERIES) {
    const source = read(...path);
    assert.match(source, /<ImmersiveGalleryShell/, `${name} is not in the shell`);
    assert.match(source, /\{\(scroll\) => \(/, `${name} does not take the scroll plumbing`);
    assert.match(source, /scroll\.onScroll/, `${name} does not report its offset`);
    assert.match(source, /scroll\.contentPaddingBottom/, `${name} does not clear the overlay`);
  }
});

test('the collapsible chrome carries the title, the actions and the filters', () => {
  for (const [name, path] of GALLERIES) {
    const source = read(...path);
    const chrome = source.indexOf('topChrome={');
    const body = source.indexOf('{(scroll) => (');
    assert.ok(chrome > 0 && chrome < body, `${name} has no top chrome`);
    // A filter row pinned over the gallery is a second header; if a surface
    // has one, it belongs above the render prop.
    for (const marker of ['<MediaFilterChips', 'styles.filters']) {
      const at = source.indexOf(marker);
      if (at > 0) {
        assert.ok(at < body, `${name} pins ${marker} over the gallery`);
      }
    }
  }
});

test('no gallery offers a Select control in normal browsing', () => {
  // Selection begins with a long-press on an item, where the hand already is.
  for (const [name, path] of GALLERIES) {
    const source = read(...path);
    assert.doesNotMatch(source, /t\('selection\.select'\)/, `${name} shows a Select button`);
    assert.doesNotMatch(source, /checkmark-circle-outline/, `${name} shows a Select icon`);
  }
});

test('no gallery offers a settings cog', () => {
  for (const [name, path] of GALLERIES) {
    assert.doesNotMatch(read(...path), /settings-outline/, `${name} still shows a gear`);
  }
});

test('the account hub is the personal entry point, and owns sync', () => {
  assert.match(read('app', '(tabs)', 'photos.tsx'), /person-circle-outline/);
  assert.match(read('app', '(tabs)', 'photos.tsx'), /router\.push\('\/account'\)/);
  const account = read('app', 'account.tsx');
  assert.match(account, /router\.push\('\/sync'\)/);
  assert.match(account, /session\.logout\(\)/);
  assert.match(account, /THEME_PREFERENCES/);
});

test('scrolling never re-renders the gallery', () => {
  // §14. A scroll handler that called setState would re-render the whole
  // gallery on every frame of every flick. The rule runs in a ref and only an
  // Animated value moves.
  const shell = read('src', 'ui', 'ImmersiveGalleryShell.tsx');
  assert.match(shell, /useRef<GalleryChromeState>/);
  assert.match(shell, /Animated\.timing/);
  assert.match(shell, /useNativeDriver: true/);
  // The only React state here is the measured chrome height and the
  // reduced-motion preference — neither changes while scrolling.
  const states = [...shell.matchAll(/useState[<(]/g)].length;
  assert.equal(states, 2, `the shell holds ${states} pieces of React state`);
});

test('hiding the chrome does not disturb the list', () => {
  // §14 again: no remount, no key change, no column recalculation. The grid
  // receives padding and a scroll handler, and nothing else about it depends
  // on whether the chrome is showing.
  const grid = read('src', 'components', 'MediaGrid.tsx');
  assert.match(grid, /key=\{columns\}/);
  assert.match(grid, /const columns = columnsForWidth\(width\)/);
  assert.doesNotMatch(grid, /chrome|hidden|collaps/i);
});

test('the bottom navigation floats, and the gallery clears it from inside', () => {
  assert.match(read('src', 'ui', 'BrandTabBar.tsx'), /position: 'absolute'/);
  assert.match(read('app', '(tabs)', '_layout.tsx'), /tabBarStyle: \{ position: 'absolute' \}/);
  // Clearance is content padding, never an opaque strip carved out beside the
  // scroll view.
  assert.match(
    read('src', 'ui', 'ImmersiveGalleryShell.tsx'),
    /contentPaddingBottom: bottomOverlayHeight \+ insets\.bottom/,
  );
});

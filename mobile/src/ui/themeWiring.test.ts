import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const HERE = dirname(fileURLToPath(import.meta.url));
const ROOT = resolve(HERE, '..', '..');
const read = (...parts: string[]): string => code(readFileSync(resolve(ROOT, ...parts), 'utf8'));

test('the theme provider wraps the whole app, including the login screen', () => {
  // Above SessionProvider on purpose: a signed-out user staring at the login
  // form is still a user with a theme, and the choice is restored from storage
  // before any account exists.
  const layout = read('app', '_layout.tsx');
  assert.match(layout, /<ThemeProvider>/);
  assert.ok(
    layout.indexOf('<ThemeProvider>') < layout.indexOf('<SessionProvider>'),
    'the theme must not depend on being signed in',
  );
});

test('the status bar is derived from the theme rather than pinned', () => {
  const layout = read('app', '_layout.tsx');
  assert.doesNotMatch(layout, /<StatusBar style="dark"/);
  assert.doesNotMatch(layout, /<StatusBar style="light"/);
  assert.match(layout, /theme === 'dark' \? 'light' : 'dark'/);
});

test('the native shell follows the system so that `system` is a real choice', () => {
  // Expo pins userInterfaceStyle to light unless told otherwise, and then
  // useColorScheme() answers 'light' forever.
  assert.match(readFileSync(resolve(ROOT, 'app.config.js'), 'utf8'), /userInterfaceStyle: 'automatic'/);
});

test('settings is reachable, and is where signing out now lives', () => {
  assert.match(read('app', '_layout.tsx'), /<Stack\.Screen name="settings"/);
  const photos = read('app', '(tabs)', 'photos.tsx');
  assert.match(photos, /router\.push\('\/settings'\)/);
  // The header had six controls and was overflowing; sign-out moved rather
  // than being duplicated.
  assert.doesNotMatch(photos, /log-out-outline/);
  assert.match(read('app', 'settings.tsx'), /session\.logout\(\)/);
});

test('tokens.ts offers no palette, so a stale colour cannot be imported', () => {
  // The enforcement is the compiler: `colors` no longer exists in tokens, so a
  // module-level colour captured at import time cannot be written by accident.
  const tokens = read('src', 'ui', 'tokens.ts');
  assert.doesNotMatch(tokens, /export const colors\b/);
  assert.doesNotMatch(tokens, /\bcolor:/);
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

test('every stylesheet that uses the palette is built per theme', () => {
  // A `StyleSheet.create` that closes over the palette outside themed() keeps
  // whichever theme happened to be active when its module first loaded — which
  // looks perfect until somebody switches.
  const offenders: string[] = [];
  for (const file of [...sources(join(ROOT, 'src')), ...sources(join(ROOT, 'app'))]) {
    const text = code(readFileSync(file, 'utf8'));
    const sheet = /const styles = StyleSheet\.create\(\{[\s\S]*?\n\}\);/.exec(text);
    if (sheet && /\bcolors\./.test(sheet[0])) offenders.push(file.slice(ROOT.length + 1));
  }
  assert.deepEqual(offenders, [], `unthemed stylesheets: ${offenders.join(', ')}`);
});

test('every explicit-extension import points at a file that exists', () => {
  // Metro resolves what is written; TypeScript is more forgiving and will
  // accept `./theme.ts` for a file called `theme.tsx`. The typecheck therefore
  // passes and the app dies at the first bundle — which is how the dark theme
  // shipped a red box to the emulator smoke test rather than a screen.
  const offenders: string[] = [];
  for (const file of [...sources(join(ROOT, 'src')), ...sources(join(ROOT, 'app'))]) {
    const text = readFileSync(file, 'utf8');
    for (const m of text.matchAll(/from '(\.[^']*\.tsx?)'/g)) {
      if (!existsSync(resolve(dirname(file), m[1]))) offenders.push(`${file.slice(ROOT.length + 1)} -> ${m[1]}`);
    }
  }
  assert.deepEqual(offenders, [], `unresolvable imports:\n${offenders.join('\n')}`);
});

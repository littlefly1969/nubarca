import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

const LAYOUT = read('app', '_layout.tsx');
const CONFIG = read('app.config.js');

/** Every surface where you BROWSE a library rather than look at one thing. */
const BROWSING = ['(tabs)', 'album/[id]', 'shared-album/[id]'];

test('browsing surfaces are portrait', () => {
  // NUBARCA-UX-01.4. Rotating a gallery changes the column count, which
  // relayouts and recycles every cell, which changes each tile's image path,
  // which empties the tile until its fetch completes. The product pays a wave
  // of placeholders for two extra columns. It is not worth it, and the answer
  // is a policy rather than more machinery inside the list.
  for (const route of BROWSING) {
    const at = LAYOUT.indexOf(`name="${route}"`);
    assert.ok(at > 0, `${route} is not declared in the stack, so it carries no policy`);
    const declaration = LAYOUT.slice(at, LAYOUT.indexOf('/>', at));
    assert.match(declaration, /orientation: 'portrait'/, `${route} is not portrait`);
  }
});

test('the viewer is free to rotate', () => {
  // The one surface where a landscape photograph is worth seeing, and the one
  // that already owns orientation-sensitive pager geometry.
  const at = LAYOUT.indexOf('name="media/[id]"');
  assert.ok(at > 0);
  const declaration = LAYOUT.slice(at, LAYOUT.indexOf('/>', at));
  assert.match(declaration, /orientation: 'all'/);
  // Its existing presentation is not collateral damage of adding the option.
  assert.match(declaration, /presentation: 'fullScreenModal'/);
  assert.match(declaration, /animation: 'fade'/);
});

test('the app stays globally permissive, so the viewer can still rotate', () => {
  // Locking the whole app to portrait would take the viewer down with it.
  // Global permission, per-screen policy.
  assert.match(CONFIG, /orientation: 'default'/);
  assert.doesNotMatch(CONFIG, /orientation: 'portrait'/);
});

test('orientation is declared, never driven at runtime', () => {
  // A controller, a listener or a focus effect can be out of step with the
  // screen it is meant to describe; a stack option cannot.
  for (const runtime of [
    /lockAsync/,
    /unlockAsync/,
    /expo-screen-orientation/,
    /addOrientationChangeListener/,
    /key=\{orientation\}/,
  ]) {
    assert.doesNotMatch(LAYOUT, runtime, `orientation is driven at runtime via ${runtime.source}`);
  }
});

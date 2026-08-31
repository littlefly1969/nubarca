// The package must stay platform-neutral (§2). Asserted, not merely intended:
// the moment a transport, a DOM reference or a framework import lands here,
// the phone and the television stop being able to load it at all.

import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const SRC = dirname(fileURLToPath(import.meta.url));

function sources(): Array<{ name: string; code: string }> {
  return readdirSync(SRC)
    .filter((f) => f.endsWith('.ts') && !f.endsWith('.test.ts'))
    .map((name) => ({
      name,
      // Comments stripped: a banned word inside the comment explaining why it
      // is banned would otherwise fail the test forever.
      code: readFileSync(join(SRC, name), 'utf8')
        .replace(/\/\*[\s\S]*?\*\//g, '')
        .split('\n')
        .filter((line) => !line.trimStart().startsWith('//'))
        .join('\n'),
    }));
}

test('no runtime, framework or platform dependency reaches the contracts', () => {
  const banned: Array<[string, RegExp]> = [
    ['React', /\bfrom ['"]react['"]/],
    ['React Native', /\bfrom ['"]react-native/],
    ['Expo', /\bfrom ['"]expo/],
    ['the DOM', /\b(window|document|localStorage|navigator)\b/],
    ['a transport', /\bfetch\s*\(|XMLHttpRequest|axios/],
    ['cookies or sessions', /\bcookie|SecureStore|credentials:/i],
    ['URLSearchParams', /URLSearchParams/],
  ];
  for (const { name, code } of sources()) {
    for (const [what, pattern] of banned) {
      assert.doesNotMatch(code, pattern, `${name} must not reach for ${what}`);
    }
  }
});

test('every import is relative and inside the package', () => {
  // A dependency on any other package would make this unloadable from one of
  // the three clients, which is the whole point of keeping it dependency-free.
  for (const { name, code } of sources()) {
    for (const match of code.matchAll(/from ['"]([^'"]+)['"]/g)) {
      const specifier = match[1];
      assert.ok(
        specifier.startsWith('./') || specifier.startsWith('../'),
        `${name} imports '${specifier}', which is not a local module`,
      );
    }
  }
});

test('relative imports carry the .ts extension Node needs', () => {
  // Node strips types only with an explicit extension; Metro and Vite accept
  // it. Dropping one here breaks the mobile and TV test runners, and nothing
  // else notices.
  for (const { name, code } of sources()) {
    for (const match of code.matchAll(/from ['"](\.[^'"]+)['"]/g)) {
      assert.match(match[1], /\.ts$/, `${name} imports '${match[1]}' without .ts`);
    }
  }
});

import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const LOGIN = code(readFileSync(resolve(ROOT, 'app', 'login.tsx'), 'utf8'));

// BRAND-APP-02 redesigned this screen's SURFACE. These assertions are about the
// half that must not have moved: a visual slice that changed how a session is
// established, or how a failure is classified, would be a security change
// wearing a design commit's clothes.

test('authentication does exactly what it did', () => {
  assert.match(LOGIN, /configureBaseUrl\(baseUrl\.trim\(\)\)/);
  assert.match(LOGIN, /session\.login\(baseUrl\.trim\(\), email\.trim\(\), password\)/);
  // configureBaseUrl must still run BEFORE the login call, or the request goes
  // to whichever server was configured last.
  assert.ok(
    LOGIN.indexOf('configureBaseUrl(baseUrl.trim())') < LOGIN.indexOf('session.login('),
    'the base URL must be configured before the login call',
  );
});

test('a 401 is still bad credentials and everything else is still the network', () => {
  assert.match(LOGIN, /err instanceof ApiError && err\.status === 401/);
  assert.match(LOGIN, /setError\(t\('login\.errorCredentials'\)\)/);
  assert.match(LOGIN, /setError\(t\('login\.errorNetwork'\)\)/);
});

test('the server stays editable and prefilled from the last one used', () => {
  assert.match(LOGIN, /getStoredBaseUrl\(\)/);
  assert.match(LOGIN, /onChangeText=\{setBaseUrl\}/);
  assert.match(LOGIN, /label=\{t\('login\.apiBaseUrl'\)\}/);
});

test('an authenticated visitor still leaves for Photos', () => {
  assert.match(LOGIN, /session\.status === 'authed'/);
  assert.match(LOGIN, /<Redirect href="\/\(tabs\)\/photos" \/>/);
});

test('keyboard and scroll behaviour survive the redesign', () => {
  assert.match(LOGIN, /KeyboardAvoidingView/);
  assert.match(LOGIN, /behavior=\{Platform\.OS === 'ios' \? 'padding' : undefined\}/);
  assert.match(LOGIN, /keyboardShouldPersistTaps="handled"/);
});

test('the screen is built from the shared primitives, not from local controls', () => {
  for (const primitive of ['BrandLockup', 'TextField', 'InlineNotice', 'Button']) {
    assert.match(LOGIN, new RegExp(`<${primitive}\\b`), `login does not use ${primitive}`);
  }
  // A raw TextInput or Pressable here would be a screen re-deciding what an
  // input or an action looks like.
  assert.doesNotMatch(LOGIN, /<TextInput\b|<Pressable\b/);
});

test('the brand is artwork, and the field labels are not shouted', () => {
  // The product name typed in the heading face is a different mark, and nobody
  // approved it.
  assert.doesNotMatch(LOGIN, /<Text[^>]*>NubArca</);
  assert.doesNotMatch(LOGIN, /textTransform: 'uppercase'/);
});

test('no deprecated alias and no local colour reaches this screen', () => {
  assert.doesNotMatch(LOGIN, /\bradii\.|\btype\.\w|colors\.surfaceMuted/);
  assert.doesNotMatch(LOGIN, /#[0-9A-Fa-f]{3,8}\b|\brgba?\(/);
});

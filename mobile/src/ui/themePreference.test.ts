import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import {
  DEFAULT_THEME_PREFERENCE,
  THEME_PREFERENCES,
  resolveTheme,
  toThemePreference,
} from './themePreference.ts';

test('dark is the product default, as on the web', () => {
  assert.equal(DEFAULT_THEME_PREFERENCE, 'dark');
});

test('the three choices are offered in the web order', () => {
  assert.deepEqual([...THEME_PREFERENCES], ['dark', 'light', 'system']);
});

test('an explicit choice ignores the operating system', () => {
  // The point of choosing: a phone in dark mode must not be able to override a
  // user who deliberately picked light, in either direction.
  assert.equal(resolveTheme('light', 'dark'), 'light');
  assert.equal(resolveTheme('dark', 'light'), 'dark');
});

test('system follows the operating system', () => {
  assert.equal(resolveTheme('system', 'dark'), 'dark');
  assert.equal(resolveTheme('system', 'light'), 'light');
});

test('an unknown system answer falls back to the product default, not to light', () => {
  // useColorScheme() answers null before the OS has been asked. Defaulting to
  // light there would flash a light app at every cold start for the users who
  // asked to follow the system on a dark phone.
  assert.equal(resolveTheme('system', null), 'dark');
  assert.equal(resolveTheme('system', undefined), 'dark');
});

test('an untrusted stored value is narrowed, never trusted', () => {
  assert.equal(toThemePreference('dark'), 'dark');
  assert.equal(toThemePreference('light'), 'light');
  assert.equal(toThemePreference('system'), 'system');
  for (const rubbish of ['DARK', '', 'auto', null, undefined, 7, {}, ['dark']]) {
    assert.equal(toThemePreference(rubbish), null, `accepted ${JSON.stringify(rubbish)}`);
  }
});

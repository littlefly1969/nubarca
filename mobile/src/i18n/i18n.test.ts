// I18n dictionary tests: the Italian dictionary is the canonical key set and
// English falls back to it. These tests pin the keys that once regressed
// (wrong-key error messages, hardcoded strings) and the subset invariant.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import it from './it.ts';
import en from './en.ts';

const REGRESSION_KEYS = [
  // The Videos tab once used gallery.whatFolder for its error message.
  'gallery.whatVideos',
  // Login errors were hardcoded Italian before these existed.
  'login.errorCredentials',
  'login.errorNetwork',
  // Logout used to be unconfirmed.
  'common.signOutConfirmBody',
  // Album save failures were silent unhandled rejections before this key.
  'albums.saveError',
] as const;

test('every regression key exists in the Italian dictionary', () => {
  for (const key of REGRESSION_KEYS) {
    assert.ok(key in it, `missing IT key: ${key}`);
    assert.ok((it as Record<string, string>)[key].length > 0);
  }
});

test('every regression key has a non-empty English translation', () => {
  for (const key of REGRESSION_KEYS) {
    const value = (en as Record<string, string | undefined>)[key];
    assert.ok(typeof value === 'string' && value.length > 0, `missing EN key: ${key}`);
  }
});

test('every English key is a known Italian key (no typos in en.ts)', () => {
  for (const key of Object.keys(en)) {
    assert.ok(key in it, `en.ts defines unknown key: ${key}`);
  }
});

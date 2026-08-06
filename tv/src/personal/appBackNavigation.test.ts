import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const app = readFileSync(new URL('../../App.tsx', import.meta.url), 'utf8');

test('the final BACK at either TV root explicitly closes the Android app', () => {
  assert.match(app, /flow\.name !== 'mode' && flow\.name !== 'pairing'/);
  assert.match(app, /BackHandler\.exitApp\(\)/);
  assert.match(app, /addEventListener\('hardwareBackPress', onBackPress\)/);
});

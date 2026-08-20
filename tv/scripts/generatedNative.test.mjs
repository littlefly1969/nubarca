// Assertions against the REAL generated Android tree, not against source.
//
// Everything here exists because source-level tests were not enough. A plugin
// constant can encode a rule perfectly and still lose to `@react-native-tvos/
// config-tv`, which copies its own banner into every density bucket from a
// DANGEROUS mod. What ships is whatever survives in android/ after the whole
// mod pipeline has run — so that is what these read.
//
//   npm run test:native     after a prebuild; fails hard if the tree is missing
//   npm test                skips with a visible reason when android/ is absent
//
// The skip is deliberate and loud: a fresh checkout has no android/ (it is
// gitignored and regenerated), and a test that silently passed there would be
// worse than no test at all.

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { existsSync, readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const tvRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const { readReleaseContract } = require(resolve(tvRoot, 'scripts/release-contract.cjs'));
const release = readReleaseContract();

const ANDROID = resolve(tvRoot, 'android');
const RES = resolve(ANDROID, 'app/src/main/res');
const MANIFEST = resolve(ANDROID, 'app/src/main/AndroidManifest.xml');

// `npm run test:native` sets this; then a missing tree is a failure, not a skip.
const REQUIRED = process.env.NUBARCA_REQUIRE_GENERATED_ANDROID === '1';
const generated = existsSync(MANIFEST);

if (REQUIRED && !generated) {
  throw new Error(
    'No generated Android project. Run `npm run tv:prebuild` first — this suite ' +
      'exists precisely to check what prebuild actually produced.',
  );
}
const when = (name, fn) => test(name, { skip: generated ? false : 'no generated android/ — run npm run tv:prebuild' }, fn);

const bannerPath = (bucket) => resolve(RES, bucket, 'tv_banner.png');

/** width/height straight out of the PNG IHDR. */
function pngSize(file) {
  const png = readFileSync(file);
  return [png.readUInt32BE(16), png.readUInt32BE(20)];
}

// --- the banner density contract ---------------------------------------------

when('the generated tree carries the banner ONLY at xhdpi', () => {
  assert.ok(existsSync(bannerPath('drawable-xhdpi')),
    'drawable-xhdpi/tv_banner.png must exist: 320x180 px IS the 2x rendering of a 160x90 dp banner');
  for (const bucket of ['drawable', 'drawable-mdpi', 'drawable-hdpi',
    'drawable-xxhdpi', 'drawable-xxxhdpi']) {
    assert.equal(existsSync(bannerPath(bucket)), false,
      `${bucket}/tv_banner.png must be absent — config-tv copies one into every bucket`);
  }
});

when('the generated xhdpi banner really is 320x180 pixels', () => {
  assert.deepEqual(pngSize(bannerPath('drawable-xhdpi')), [320, 180]);
});

when('the 1280x720 promotional artwork never reached the generated tree', () => {
  // It is not a density variant of a 160x90 dp banner at all: that would need an
  // 8x bucket and Android stops at 4x.
  for (const bucket of ['drawable', 'drawable-mdpi', 'drawable-hdpi',
    'drawable-xhdpi', 'drawable-xxhdpi', 'drawable-xxxhdpi']) {
    if (!existsSync(bannerPath(bucket))) continue;
    assert.notDeepEqual(pngSize(bannerPath(bucket)), [1280, 720],
      `${bucket} holds the Appstore artwork, which is not a manifest-banner resource`);
  }
});

// --- the manifest contract accepted in 1.0.7 ---------------------------------

const manifest = () => readFileSync(MANIFEST, 'utf8');
const mainActivity = () =>
  /<activity[^>]*MainActivity[\s\S]*?<\/activity>/.exec(manifest())[0];
const mainFilter = () =>
  [...mainActivity().matchAll(/<intent-filter>[\s\S]*?<\/intent-filter>/g)]
    .map((m) => m[0])
    .filter((f) => f.includes('android.intent.action.MAIN'));

const occurrences = (haystack, name) =>
  haystack.split(`"${name}"`).length - 1;

when('the launcher registration accepted in 1.0.7 is unchanged', () => {
  const filters = mainFilter();
  assert.equal(filters.length, 1, 'exactly one MAIN intent-filter');
  const [filter] = filters;
  assert.equal(occurrences(filter, 'android.intent.action.MAIN'), 1);
  assert.equal(occurrences(filter, 'android.intent.category.LAUNCHER'), 1);
  assert.equal(occurrences(filter, 'android.intent.category.LEANBACK_LAUNCHER'), 1);
});

when('both banner declarations survive the fix', () => {
  // Fixing the RESOURCE must not quietly drop the DECLARATIONS that point at it.
  assert.equal(occurrences(manifest(), '@drawable/tv_banner'), 2,
    'one on <application>, one on MainActivity');
  assert.match(mainActivity(), /android:banner="@drawable\/tv_banner"/);
});

when('the install permission contract is unchanged', () => {
  assert.equal(occurrences(manifest(), 'android.permission.REQUEST_INSTALL_PACKAGES'), 1);
  assert.equal(occurrences(manifest(), 'android.permission.INSTALL_PACKAGES'), 0);
  assert.equal(manifest().includes('UPDATE_PACKAGES_WITHOUT_USER_ACTION'), false);
});

when('the generated project still builds the accepted release identity', () => {
  const gradle = readFileSync(resolve(ANDROID, 'app/build.gradle'), 'utf8');
  assert.match(gradle, new RegExp(`versionCode ${release.versionCode}\\b`));
  assert.match(gradle, new RegExp(`versionName ['"]${release.version.replace(/\./g, '\\.')}['"]`));
  assert.match(gradle, new RegExp(`applicationId ['"]${release.package.replace(/\./g, '\\.')}['"]`));
});

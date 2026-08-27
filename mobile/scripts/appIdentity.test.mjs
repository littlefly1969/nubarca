import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import test from 'node:test';

const require = createRequire(import.meta.url);
const mobileRoot = resolve(import.meta.dirname, '..');
const repoRoot = resolve(mobileRoot, '..');
const contract = require(resolve(mobileRoot, 'release-contract.json'));
const packageJson = require(resolve(mobileRoot, 'package.json'));
const tvContract = require(resolve(repoRoot, 'tv/release-contract.json'));
const { normalizePublicOrigin, readReleaseContract } = require('./release-contract.cjs');

function productionConfig() {
  const oldNodeEnv = process.env.NODE_ENV;
  const oldOrigin = process.env.NUBARCA_PUBLIC_ORIGIN;
  try {
    process.env.NODE_ENV = 'production';
    process.env.NUBARCA_PUBLIC_ORIGIN = 'https://example.invalid';
    delete require.cache[require.resolve(resolve(mobileRoot, 'app.config.js'))];
    return require(resolve(mobileRoot, 'app.config.js')).expo;
  } finally {
    if (oldNodeEnv === undefined) delete process.env.NODE_ENV;
    else process.env.NODE_ENV = oldNodeEnv;
    if (oldOrigin === undefined) delete process.env.NUBARCA_PUBLIC_ORIGIN;
    else process.env.NUBARCA_PUBLIC_ORIGIN = oldOrigin;
  }
}

test('the tracked mobile release contract is valid and package identity is permanent', () => {
  assert.deepEqual(readReleaseContract(), contract);
  assert.equal(contract.package, 'it.littlefly.nubarca');
  assert.notEqual(contract.package, tvContract.package);
  assert.equal(contract.version, packageJson.version);
  assert.equal(contract.targetSdk, 36);
  assert.ok(contract.minSdk <= 24, 'Android 7 compatibility floor must not rise silently');
});

test('production Expo config is derived from the release contract', () => {
  const expo = productionConfig();
  assert.equal(expo.name, contract.applicationName);
  assert.equal(expo.version, contract.version);
  assert.equal(expo.android.package, contract.package);
  assert.equal(expo.android.versionCode, contract.versionCode);
  assert.equal(expo.extra.releaseVersion, contract.version);
  assert.equal(expo.extra.releaseVersionCode, contract.versionCode);
  assert.equal(expo.extra.apiBaseUrl, 'https://example.invalid');
  assert.equal(expo.android.usesCleartextTraffic, false);
  assert.equal(expo.android.allowBackup, false);
  assert.ok(expo.plugins.includes('./plugins/withReleaseSigning'));
});

test('mobile launcher assets are approved byte-exact brand copies', () => {
  const pairs = [
    ['assets/brand/nubarca-expo-app-icon-1024.png',
      '../assets/brand/nubarca/runtime/pwa/nubarca-expo-app-icon-1024.png'],
    ['assets/brand/nubarca-android-adaptive-foreground-432.png',
      '../assets/brand/nubarca/runtime/tv/nubarca-android-adaptive-foreground-432.png'],
  ];
  for (const [consumer, canonical] of pairs) {
    const digest = (path) => createHash('sha256').update(readFileSync(resolve(mobileRoot, path))).digest('hex');
    assert.equal(digest(consumer), digest(canonical), consumer);
  }
});

test('production origin normalization accepts one HTTPS origin only', () => {
  assert.equal(normalizePublicOrigin(' https://example.invalid/ '), 'https://example.invalid');
  for (const invalid of [
    '', 'http://example.invalid', 'https://user@example.invalid',
    'https://example.invalid/path', 'https://example.invalid?q=1',
    'https://example.invalid/#fragment', 'not-a-url',
  ]) {
    assert.throws(() => normalizePublicOrigin(invalid));
  }
});

const TEMPLATE_BUILD_GRADLE = `android {
    signingConfigs {
        debug {
            storeFile file('debug.keystore')
            storePassword 'android'
            keyAlias 'androiddebugkey'
            keyPassword 'android'
        }
    }
    buildTypes {
        debug {
            signingConfig signingConfigs.debug
        }
        release {
            // Caution! In production, you need to generate your own keystore file.
            // see https://reactnative.dev/docs/signed-apk-android.
            signingConfig signingConfigs.debug
            minifyEnabled false
        }
    }
}`;

const withReleaseSigning = require(resolve(mobileRoot, 'plugins/withReleaseSigning.js'));

async function applySigningPlugin(contents) {
  const { mods } = withReleaseSigning({});
  const applied = await mods.android.appBuildGradle({
    modResults: { contents, language: 'groovy', path: 'android/app/build.gradle' },
  });
  return applied.modResults.contents;
}

test('release signing uses only the dedicated mobile upload key', async () => {
  const generated = await applySigningPlugin(TEMPLATE_BUILD_GRADLE);
  assert.match(generated, /release \{\n {12}signingConfig signingConfigs\.release/);
  assert.doesNotMatch(generated.split('buildTypes')[1], /release \{[\s\S]*?signingConfig signingConfigs\.debug/);
  for (const property of [
    'NUBARCA_MOBILE_UPLOAD_STORE_FILE',
    'NUBARCA_MOBILE_UPLOAD_STORE_PASSWORD',
    'NUBARCA_MOBILE_UPLOAD_KEY_ALIAS',
    'NUBARCA_MOBILE_UPLOAD_KEY_PASSWORD',
  ]) assert.ok(generated.includes(property), property);
  assert.match(generated, /enableV1Signing true/);
  assert.match(generated, /enableV2Signing true/);
  assert.match(generated, /enableV3Signing true/);
});

test('release signing fails closed and remains idempotent', async () => {
  const once = await applySigningPlugin(TEMPLATE_BUILD_GRADLE);
  const twice = await applySigningPlugin(once);
  assert.equal(once, twice);
  assert.match(once, /buildsRelease && missingReleaseSigning/);
  assert.match(once, /throw new GradleException/);
  await assert.rejects(
    () => applySigningPlugin('android { buildTypes { release { } } }'),
    /no longer matches/,
  );
});

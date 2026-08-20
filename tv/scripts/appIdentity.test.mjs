// Pins the NubArca TV application identity and the native release-signing
// rewrite, so neither can drift back by accident.
//
// The identity assertions are deliberately literal. An Android applicationId
// has no in-place rename: getting one of these values wrong ships an app that
// installs beside the real one instead of updating it.

import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { dirname, join, relative, resolve } from 'node:path';
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { spawnSync } from 'node:child_process';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const tvRoot = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const { readReleaseContract } = require(resolve(tvRoot, 'scripts/release-contract.cjs'));
const release = readReleaseContract();

/** Load app.config.js with a controlled environment. */
function loadConfig(env = {}) {
  const previous = { ...process.env };
  for (const key of Object.keys(process.env)) delete process.env[key];
  Object.assign(process.env, { NODE_ENV: 'development' }, env);
  try {
    delete require.cache[require.resolve(resolve(tvRoot, 'app.config.js'))];
    return require(resolve(tvRoot, 'app.config.js'))().expo;
  } finally {
    for (const key of Object.keys(process.env)) delete process.env[key];
    Object.assign(process.env, previous);
  }
}

test('the application identity is the final NubArca TV identity', () => {
  const expo = loadConfig();
  const packageJson = JSON.parse(readFileSync(resolve(tvRoot, 'package.json'), 'utf8'));
  assert.equal(expo.name, release.applicationName);
  assert.equal(expo.slug, 'nubarca-tv');
  assert.equal(expo.scheme, 'nubarca-tv');
  assert.equal(expo.version, release.version);
  assert.equal(expo.android.package, release.package);
  assert.equal(expo.android.versionCode, release.versionCode);
  assert.equal(packageJson.version, release.version);
});

test('the tracked release contract loader fails closed on malformed identity', () => {
  const directory = mkdtempSync(join(tmpdir(), 'nubarca-tv-release-contract-'));
  try {
    const malformed = join(directory, 'release-contract.json');
    writeFileSync(malformed, JSON.stringify({ ...release, versionCode: 0 }));
    assert.throws(() => readReleaseContract(malformed), /positive integer/i);
    writeFileSync(malformed, JSON.stringify({ ...release, unexpected: true }));
    assert.throws(() => readReleaseContract(malformed), /contain exactly/i);
    writeFileSync(malformed, JSON.stringify({ ...release, updatePath: '/api/%2e%2e/escape' }));
    assert.throws(() => readReleaseContract(malformed), /unsafe|invalid encoded/i);
    writeFileSync(malformed, '{');
    assert.throws(() => readReleaseContract(malformed), /Unable to read/i);
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test('APK validation and publication consume one release contract and one validator', () => {
  const validator = readFileSync(resolve(tvRoot, '../deploy/validate-tv-apk.sh'), 'utf8');
  const publisher = readFileSync(resolve(tvRoot, '../deploy/publish-tv-apk.sh'), 'utf8');
  assert.match(validator, /release-contract\.cjs/);
  assert.match(validator, /expected_signer_sha256="\$\{release_values\[7\]\}"/);
  assert.doesNotMatch(validator, new RegExp(release.apkSignerSha256));
  assert.match(publisher, /deploy\/validate-tv-apk\.sh/);
  assert.doesNotMatch(publisher, /apksigner|apkanalyzer|aapt2/);
});

// A NubArca version bump must move the PROJECT version and nothing else. A
// blanket text replace of the old version across package-lock.json rewrote four
// DEPENDENCY version fields that happened to carry the same number, leaving
// entries claiming a version their own resolved URL and integrity hash
// contradict. Nothing else in the suite reads the lockfile, so it stayed green
// until `npm ci` at the release build. Regenerate with npm; never sed.
test('a release bump cannot silently rewrite dependency versions', () => {
  const lock = JSON.parse(readFileSync(resolve(tvRoot, 'package-lock.json'), 'utf8'));
  const packageJson = JSON.parse(readFileSync(resolve(tvRoot, 'package.json'), 'utf8'));

  assert.equal(lock.version, release.version, 'lockfile root version must track the release contract');
  assert.equal(lock.packages[''].version, release.version);
  assert.equal(packageJson.version, release.version);

  const wrong = [];
  for (const [name, entry] of Object.entries(lock.packages)) {
    if (!name || !entry.resolved || !entry.version) continue;
    // The registry tarball name ends with the exact version it contains.
    const inUrl = /-(\d+\.\d+\.\d+[^/]*)\.tgz$/.exec(entry.resolved)?.[1];
    if (inUrl && inUrl !== entry.version) {
      wrong.push(`${name}: version ${entry.version} but tarball is ${inUrl}`);
    }
  }
  assert.deepEqual(wrong, [], 'dependency versions disagree with their own tarballs');
});

test('the reserved mobile applicationId is not taken by the TV app', () => {
  // NubArca (mobile) and NubArca TV are separate applications sharing one
  // backend. it.littlefly.nubarca belongs to the future mobile binary.
  const expo = loadConfig();
  assert.notEqual(expo.android.package, 'it.littlefly.nubarca');
  assert.ok(expo.android.package.startsWith('it.littlefly.nubarca.'));
});

test('no user-visible identifier carries the former product name', () => {
  const expo = loadConfig();
  for (const [field, value] of Object.entries({
    name: expo.name,
    slug: expo.slug,
    scheme: expo.scheme,
    package: expo.android.package,
    runtimeVersion: expo.runtimeVersion,
  })) {
    assert.doesNotMatch(value, /nano[_-]?cloud/i, `${field} still carries the former product name`);
  }
});

test('the default OTA runtime belongs to the new native series', () => {
  const expo = loadConfig();
  assert.equal(expo.runtimeVersion, release.runtimeVersion);
  // A runtime name identifies one native contract for ONE application. It must
  // carry this app's series, so a bundle can never be offered to an install of a
  // different package that happens to ask for a bare `tv-native-*` runtime.
  assert.ok(expo.runtimeVersion.startsWith('nubarca-tv-native-'));
});

test('a production https base URL builds with cleartext traffic disabled', () => {
  const expo = loadConfig({ EXPO_PUBLIC_NUBARCA_API_BASE_URL: 'https://nubarca.example.com' });
  assert.equal(expo.android.usesCleartextTraffic, false);
  assert.equal(expo.extra.apiBaseUrl, 'https://nubarca.example.com');
  assert.equal(expo.updates.url, 'https://nubarca.example.com/api/tv-app/updates');
});

test('development configuration remains usable without signing material', () => {
  assert.doesNotThrow(() => loadConfig({ NODE_ENV: 'development' }));
});

test('production configuration fails closed without every release input', () => {
  assert.throws(() => loadConfig({ NODE_ENV: 'production' }), /PUBLIC_ORIGIN is required/i);
  assert.throws(() => loadConfig({ NODE_ENV: 'production', NUBARCA_PUBLIC_ORIGIN: 'https://nubarca.example.com' }),
    /OTA_CERTIFICATE is required/i);
});

// The pinned production origin is operator configuration rather than a source
// constant, so the pin is only as strong as its fail-closed behaviour. These
// cases are the reason externalising it did not weaken the guard that exists
// because a release APK once shipped pointing at a LAN dev default.
test('production pins the API base URL to the operator-supplied origin', () => {
  const base = { NODE_ENV: 'production' };
  // An unset origin refuses the build outright.
  assert.throws(() => loadConfig({
    ...base,
  }), /PUBLIC_ORIGIN is required/i);
  // A plaintext origin is refused.
  assert.throws(() => loadConfig({
    ...base,
    EXPO_PUBLIC_NUBARCA_API_BASE_URL: 'http://nubarca.example.com',
    NUBARCA_PUBLIC_ORIGIN: 'http://nubarca.example.com',
  }), /must be an https/i);
  // A base URL that disagrees with the pinned origin is refused, which is the
  // exact failure the guard was written for.
  assert.throws(() => loadConfig({
    ...base,
    EXPO_PUBLIC_NUBARCA_API_BASE_URL: 'https://somewhere-else.example.com',
    NUBARCA_PUBLIC_ORIGIN: 'https://nubarca.example.com',
  }), /must be exactly https:\/\/nubarca\.example\.com/i);
});

test('production requires the OTA public certificate', () => {
  assert.throws(() => loadConfig({
    NODE_ENV: 'production',
    NUBARCA_PUBLIC_ORIGIN: 'https://nubarca.example.com',
  }), /OTA_CERTIFICATE is required/i);
});

test('the OTA certificate path is normalized relative to the TV project', () => {
  const certificateRoot = mkdtempSync(join(tmpdir(), 'nubarca-tv-config-cert-'));
  try {
    const key = join(certificateRoot, 'key.pem');
    const certificate = join(certificateRoot, 'certificate.pem');
    assert.equal(spawnSync('openssl', ['genpkey', '-quiet', '-algorithm', 'RSA', '-pkeyopt', 'rsa_keygen_bits:2048', '-out', key]).status, 0);
    assert.equal(spawnSync('openssl', ['req', '-x509', '-new', '-key', key, '-out', certificate, '-days', '1',
      '-subj', '/CN=NubArca TV OTA Test', '-addext', 'keyUsage=critical,digitalSignature',
      '-addext', 'extendedKeyUsage=critical,codeSigning']).status, 0);
    const expo = loadConfig({ NUBARCA_TV_OTA_CERTIFICATE: certificate });
    assert.equal(expo.updates.codeSigningCertificate, relative(tvRoot, resolve(certificate)));
    assert.deepEqual(expo.updates.codeSigningMetadata, { keyid: 'main', alg: 'rsa-v1_5-sha256' });
    const production = loadConfig({
      NODE_ENV: 'production',
      NUBARCA_PUBLIC_ORIGIN: 'https://nubarca.example.com',
      NUBARCA_TV_OTA_CERTIFICATE: certificate,
    });
    assert.equal(production.extra.apiBaseUrl, 'https://nubarca.example.com');
    assert.equal(production.updates.url, `https://nubarca.example.com${release.updatePath}`);
    assert.equal(production.runtimeVersion, release.runtimeVersion);
    assert.equal(production.updates.requestHeaders['expo-channel-name'], release.channel);
    const appConfigSource = readFileSync(resolve(tvRoot, 'app.config.js'), 'utf8');
    assert.doesNotMatch(appConfigSource, /NUBARCA_TV_RELEASE_STORE_/);
  } finally {
    rmSync(certificateRoot, { recursive: true, force: true });
  }
});

test('the installed session storage identity remains unchanged', () => {
  const sessionCookie = readFileSync(resolve(tvRoot, 'src/api/sessionCookie.ts'), 'utf8');
  assert.match(sessionCookie, /TV_SESSION_STORAGE_KEY = 'nubarca\.tv\.session\.cookie'/);
});

test('the TV app stays a leanback app that does not require a touchscreen', () => {
  const expo = loadConfig();
  const configTv = expo.plugins.find((p) => Array.isArray(p) && p[0] === '@react-native-tvos/config-tv');
  assert.ok(configTv, 'the TV config plugin must stay registered');
  assert.equal(configTv[1].isTV, true);
  assert.equal(configTv[1].androidTVRequired, true);
  assert.match(configTv[1].androidTVBanner, /nubarca-android-tv-banner-320x180\.png$/);
});

const withFireTvBanner = require(resolve(tvRoot, 'plugins/withFireTvBanner.js'));

/** The template manifest prebuild writes, before this plugin touches it. */
function templateManifest() {
  return {
    manifest: {
      application: [{
        $: { 'android:name': '.MainApplication' },
        activity: [{
          $: { 'android:name': '.MainActivity', 'android:screenOrientation': 'landscape',
               'android:exported': 'true' },
          'intent-filter': [{
            action: [{ $: { 'android:name': 'android.intent.action.MAIN' } }],
            category: [{ $: { 'android:name': 'android.intent.category.LAUNCHER' } }],
          }],
        }],
      }],
    },
  };
}

async function applyFireTvManifest(manifest = templateManifest()) {
  const { mods } = withFireTvBanner({});
  const applied = await mods.android.manifest({ modResults: manifest });
  return applied.modResults.manifest.application[0];
}

const names = (entries) => (entries ?? []).map((e) => e.$['android:name']);
const countOf = (entries, name) => names(entries).filter((n) => n === name).length;

test('the Fire TV activity exposes the rectangular banner', async () => {
  const application = await applyFireTvManifest();
  const activity = application.activity[0];
  assert.equal(application.$['android:banner'], '@drawable/tv_banner');
  assert.equal(activity.$['android:banner'], '@drawable/tv_banner');
});

// REGISTRATION. This plugin used to STRIP android.intent.category.LAUNCHER,
// guessing it was what made Fire OS draw a square icon instead of the banner.
// Physical 1.0.6 acceptance disproved it twice over: the square stayed square,
// and the app no longer appeared in the Fire TV Applications library after an
// ordinary in-place update until "Move application" forced a launcher refresh.
// Amazon's own samples declare both categories, so both are declared here.
test('the launcher activity declares BOTH launcher categories exactly once', async () => {
  const activity = (await applyFireTvManifest()).activity[0];
  const filters = activity['intent-filter'];
  assert.equal(filters.length, 1, 'no second MAIN intent-filter may be introduced');
  const [filter] = filters;
  assert.equal(countOf(filter.action, 'android.intent.action.MAIN'), 1);
  assert.equal(countOf(filter.category, 'android.intent.category.LAUNCHER'), 1);
  assert.equal(countOf(filter.category, 'android.intent.category.LEANBACK_LAUNCHER'), 1);
  assert.deepEqual(new Set(names(filter.category)), new Set([
    'android.intent.category.LAUNCHER',
    'android.intent.category.LEANBACK_LAUNCHER',
  ]));
});

test('repeated application never duplicates a launcher category', async () => {
  // prebuild re-applies every plugin from scratch, but a config can also be
  // evaluated more than once in one run; accumulating a second LAUNCHER would
  // be an invalid manifest.
  let manifest = templateManifest();
  for (let pass = 0; pass < 3; pass += 1) {
    const { mods } = withFireTvBanner({});
    manifest = (await mods.android.manifest({ modResults: manifest })).modResults;
  }
  const filter = manifest.manifest.application[0].activity[0]['intent-filter'][0];
  assert.equal(countOf(filter.category, 'android.intent.category.LAUNCHER'), 1);
  assert.equal(countOf(filter.category, 'android.intent.category.LEANBACK_LAUNCHER'), 1);
  assert.equal(countOf(filter.action, 'android.intent.action.MAIN'), 1);
});

test('a manifest that already carries both categories is left alone', async () => {
  const manifest = templateManifest();
  manifest.manifest.application[0].activity[0]['intent-filter'][0].category.push(
    { $: { 'android:name': 'android.intent.category.LEANBACK_LAUNCHER' } });
  const activity = (await applyFireTvManifest(manifest)).activity[0];
  assert.deepEqual(names(activity['intent-filter'][0].category), [
    'android.intent.category.LAUNCHER',
    'android.intent.category.LEANBACK_LAUNCHER',
  ]);
});

test('the plugin preserves activity attributes it did not set', async () => {
  // Deliberately NOT phrased as "the build is landscape": these attributes come
  // from the fixture, so all this can honestly prove is that rebuilding the
  // category list does not drop the attributes around it. The generated
  // manifest is the authority for what the build actually declares.
  const activity = (await applyFireTvManifest()).activity[0];
  assert.equal(activity.$['android:screenOrientation'], 'landscape');
  assert.equal(activity.$['android:exported'], 'true');
  assert.equal(activity.$['android:name'], '.MainActivity');
});

test('the plugin refuses a MainActivity with no MAIN intent', async () => {
  const manifest = templateManifest();
  manifest.manifest.application[0].activity[0]['intent-filter'] = [];
  await assert.rejects(() => applyFireTvManifest(manifest), /no MAIN intent/);
});

// --- Fire TV banner density contract ----------------------------------------

// THE RULE, and the specific wrong rule this must never drift back to.
//
// The Android TV banner spec is 320x180 px AT XHDPI, i.e. 160x90 dp. An earlier
// version of the plugin read it as "320x180 dp" and was then self-consistent
// with that wrong premise: 320x180 -> mdpi (1x) and the 1280x720 Fire TV
// artwork -> xxxhdpi (4x), since 1280/4 = 320. Both are wrong by the same
// factor of two, which is exactly why the arithmetic looked convincing — and on
// a Fire TV (xhdpi) the device resolved to the 1280x720 entry, rescaled it by
// 2/4, and drew the banner at twice its intended dp size.
const bannerBuckets = () => {
  const source = readFileSync(resolve(tvRoot, 'plugins/withFireTvBanner.js'), 'utf8');
  const block = /const BANNER_BY_BUCKET = \{([\s\S]*?)\n\};/.exec(source)[1];
  const buckets = {};
  for (const [, bucket, asset] of block.matchAll(
    /^\s*'?([A-Za-z-]+)'?:\s*(null|'([^']+)')/gm)) {
    buckets[bucket] = asset === 'null' ? null : /'([^']+)'/.exec(asset)[1];
  }
  return buckets;
};

test('the 320x180 banner is the XHDPI resource, never mdpi', () => {
  const buckets = bannerBuckets();
  assert.equal(buckets['drawable-xhdpi'], 'nubarca-android-tv-banner-320x180.png',
    '320x180 px is the 2x rendering of a 160x90 dp banner and belongs in xhdpi');
  assert.equal(buckets['drawable-mdpi'], null,
    'the retired "320x180 dp" reading put this file in mdpi — it must not come back');
});

test('the 1280x720 Amazon artwork is never a tv_banner density bucket', () => {
  // A 160x90 dp banner would need an 8x bucket to be 1280x720, and Android's
  // ladder stops at 4x. That asset is the Appstore/promotional artwork: it stays
  // in the brand package and is not an Android manifest-banner resource.
  const buckets = bannerBuckets();
  for (const [bucket, asset] of Object.entries(buckets)) {
    assert.notEqual(asset, 'nubarca-fire-tv-banner-1280x720.png',
      `the 1280x720 promotional asset must not be copied into ${bucket}`);
  }
});

test('exactly one bucket carries a banner and every other one is emptied', () => {
  const buckets = bannerBuckets();
  const carrying = Object.entries(buckets).filter(([, asset]) => asset !== null);
  assert.deepEqual(carrying, [['drawable-xhdpi', 'nubarca-android-tv-banner-320x180.png']]);
  // config-tv copies its banner into every bucket, so each of the others must be
  // explicitly listed for deletion — an omitted bucket keeps a stale bitmap.
  for (const bucket of ['drawable', 'drawable-mdpi', 'drawable-hdpi',
    'drawable-xxhdpi', 'drawable-xxxhdpi']) {
    assert.equal(buckets[bucket], null, `${bucket} must be explicitly emptied`);
  }
});

test('the approved banner asset really is 320x180 pixels', () => {
  // The bucket is only correct if the FILE is what its name claims.
  const png = readFileSync(resolve(tvRoot, 'assets/brand/nubarca-android-tv-banner-320x180.png'));
  assert.equal(png.readUInt32BE(16), 320);
  assert.equal(png.readUInt32BE(20), 180);
});

// --- native generation -------------------------------------------------------

// The exact React Native template text that prebuild writes. The plugin rewrites
// these anchors; if the template changes, the plugin must fail loudly rather
// than leave the debug key in place.
const TEMPLATE_BUILD_GRADLE = `android {
    namespace 'it.littlefly.nubarca.tv'
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
            minifyEnabled enableMinifyInReleaseBuilds
        }
    }
}
`;

const withReleaseSigning = require(resolve(tvRoot, 'plugins/withReleaseSigning.js'));

/**
 * Run the plugin's build.gradle mod against fixture text.
 *
 * `withAppBuildGradle` registers the mod at mods.android.appBuildGradle; the
 * pipeline later calls it with the config carrying modResults. Doing that here
 * exercises the real plugin body without needing a generated android/ project.
 */
async function applyPlugin(contents) {
  const { mods } = withReleaseSigning({});
  const applied = await mods.android.appBuildGradle({
    modResults: { contents, language: 'groovy', path: 'android/app/build.gradle' },
  });
  return applied.modResults.contents;
}

test('the release build type is rewired away from the debug keystore', async () => {
  const result = await applyPlugin(TEMPLATE_BUILD_GRADLE);
  assert.match(result, /release \{\n {12}signingConfig signingConfigs\.release/);
  // The debug build type keeps the debug key; only release moves.
  assert.match(result, /debug \{\n {12}signingConfig signingConfigs\.debug/);
  assert.doesNotMatch(
    result.split('buildTypes')[1],
    /release \{[\s\S]*?signingConfig signingConfigs\.debug/,
    'the release build type must not reference the debug signing config',
  );
});

test('the generated release signing config reads operator-supplied properties', async () => {
  const result = await applyPlugin(TEMPLATE_BUILD_GRADLE);
  for (const property of [
    'NUBARCA_TV_RELEASE_STORE_FILE',
    'NUBARCA_TV_RELEASE_STORE_PASSWORD',
    'NUBARCA_TV_RELEASE_KEY_ALIAS',
    'NUBARCA_TV_RELEASE_KEY_PASSWORD',
  ]) {
    assert.ok(result.includes(property), `${property} must be read by the release signing config`);
  }
  // No key material may ever be inlined into the generated project.
  assert.doesNotMatch(result, /storePassword\s+'(?!android')/);
});

test('release signing enables v1, v2 and v3 schemes', async () => {
  const result = await applyPlugin(TEMPLATE_BUILD_GRADLE);
  assert.match(result, /enableV1Signing true/);
  assert.match(result, /enableV2Signing true/);
  assert.match(result, /enableV3Signing true/);
});

test('a release build without a configured key fails instead of falling back', async () => {
  const result = await applyPlugin(TEMPLATE_BUILD_GRADLE);
  assert.match(result, /gradle\.taskGraph\.whenReady/);
  assert.match(result, /it\.name ==~ \/\(assemble\|bundle\|package\)Release\//);
  assert.match(result, /throw new GradleException/);
  assert.match(result, /buildsRelease && missingReleaseSigning/);
  assert.match(result, /signing\.storeFile == null \|\| !signing\.storeFile\.exists\(\)/);
  assert.match(result, /!signing\.storePassword \|\| !signing\.keyAlias \|\| !signing\.keyPassword/);
});

test('applying the plugin twice does not duplicate the signing config', async () => {
  const once = await applyPlugin(TEMPLATE_BUILD_GRADLE);
  const twice = await applyPlugin(once);
  assert.equal(once, twice);
});

test('the plugin refuses to run against an unrecognised template', async () => {
  await assert.rejects(
    () => applyPlugin('android {\n    buildTypes {\n        release { }\n    }\n}\n'),
    /no longer matches the expected template/,
  );
});

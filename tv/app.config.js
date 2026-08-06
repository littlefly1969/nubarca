// Dynamic Expo config for the NubArca TV app.
//
// The API base URL is configurable for real Fire Stick / Android TV testing
// against a production server, WITHOUT hardcoding any host or secret in source:
//
//   EXPO_PUBLIC_NUBARCA_API_BASE_URL   (preferred; also readable at runtime via
//                                       process.env.* since it is an EXPO_PUBLIC_ var)
//   NUBARCA_TV_API_BASE_URL            (build-time alias, config only)
//
// When neither is set, a loopback dev default is used (plain http, cleartext) so
// the normal dev workflow keeps working. A physical Fire Stick / Android TV
// cannot reach the workstation's loopback address, so device testing always sets
// one of the variables above to the workstation's own LAN address — that address
// belongs to whoever is developing and is never baked into source.
//
// Point the app at production with:
//
//   EXPO_PUBLIC_NUBARCA_API_BASE_URL="$NUBARCA_PUBLIC_ORIGIN" \
//     npm run tv:prebuild && (cd android && ./gradlew assembleRelease)
//
// A release build additionally requires the NubArca TV release signing key; see
// plugins/withReleaseSigning.js and docs/tv-apk-distribution.md.
//
// Cleartext (unencrypted http) traffic is enabled ONLY when the resolved base
// URL is http:// (dev on the LAN). An https:// production base URL builds with
// cleartext DISABLED — production never requires cleartext.

const DEV_DEFAULT_BASE_URL = 'http://localhost:5177';
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { validateCodeSigningCertificate } = require('./scripts/code-signing-certificate.cjs');

const RELEASE_VERSION = '1.0.1';
const RELEASE_VERSION_CODE = 2;
const RELEASE_RUNTIME = 'nubarca-tv-native-2';
const RELEASE_CHANNEL = 'production';

// The exact origin this installation's release APK must talk to. It is supplied
// by the operator and deliberately NOT hardcoded: an installation-specific host
// is deployment configuration, not product source, and the exported repository
// must not name one.
//
// The pin it backs stays FAIL-CLOSED. A production build with the variable unset
// throws below rather than accepting whatever base URL happens to be exported —
// which is the failure mode that once produced a perfectly signed APK unable to
// reach any server.
const releaseOrigin =
  process.env.NUBARCA_PUBLIC_ORIGIN?.trim().replace(/\/$/, '') || null;
const releaseUpdateUrl = releaseOrigin ? `${releaseOrigin}/api/tv-app/updates` : null;
const RELEASE_SIGNING_INPUTS = [
  'NUBARCA_TV_RELEASE_STORE_FILE',
  'NUBARCA_TV_RELEASE_STORE_PASSWORD',
  'NUBARCA_TV_RELEASE_KEY_ALIAS',
  'NUBARCA_TV_RELEASE_KEY_PASSWORD',
];

function readGradleProperties() {
  const gradleHome = process.env.GRADLE_USER_HOME || path.join(os.homedir(), '.gradle');
  const propertiesPath = path.join(gradleHome, 'gradle.properties');
  if (!fs.existsSync(propertiesPath)) return {};
  return Object.fromEntries(
    fs.readFileSync(propertiesPath, 'utf8').split(/\r?\n/)
      .map((line) => line.trim())
      .filter((line) => line && !line.startsWith('#') && !line.startsWith('!'))
      .map((line) => {
        const separator = line.search(/[=:]/);
        return separator < 0
          ? [line, '']
          : [line.slice(0, separator).trim(), line.slice(separator + 1).trim()];
      }),
  );
}

const explicitBaseUrl =
  process.env.EXPO_PUBLIC_NUBARCA_API_BASE_URL || process.env.NUBARCA_TV_API_BASE_URL;

// This config is evaluated TWICE: once by `expo prebuild`, and again by the
// Gradle JS-bundling step that writes assets/app.config into the APK. Only the
// second one decides what the shipped app talks to. Exporting the base URL for
// prebuild alone silently produces a release APK whose manifest is correct in
// every respect — right package, label, leanback, signature — but whose bundle
// points at DEV_DEFAULT_BASE_URL, with cleartext already disabled. The result
// installs, launches and can never reach a server.
//
// So a production bundle must not be allowed to fall back. NODE_ENV is
// 'production' during release bundling and in the documented build procedure.
if (!explicitBaseUrl && process.env.NODE_ENV === 'production') {
  throw new Error(
    'EXPO_PUBLIC_NUBARCA_API_BASE_URL is required for a production build.\n' +
      'Export it in the SAME shell that runs Gradle, not only for prebuild — the\n' +
      'JS bundle is produced by the Gradle build. Refusing to embed the LAN dev\n' +
      `default (${DEV_DEFAULT_BASE_URL}) into a production bundle.`,
  );
}

const apiBaseUrl = (explicitBaseUrl || DEV_DEFAULT_BASE_URL).replace(/\/$/, '');

// Only permit cleartext http on non-https (LAN dev) targets. A production
// https:// base URL does not need — and does not get — cleartext traffic.
const usesCleartextTraffic = apiBaseUrl.startsWith('http://');
const explicitUpdateUrl = process.env.NUBARCA_TV_OTA_UPDATE_URL;
const updateUrl = (explicitUpdateUrl || `${apiBaseUrl}/api/tv-app/updates`).replace(/\/$/, '');
// This value identifies one exact native ABI/configuration contract. Increment
// it before every build containing native or build-time environment changes.
//
// The `nubarca-tv-native-*` series belongs to the NubArca TV application id
// (it.littlefly.nubarca.tv) and starts over at 1. It is deliberately disjoint
// from the retired `tv-native-*` series, which belongs to the previous TV
// package: a device still running that package asks for `tv-native-3` and must
// never be served a bundle built for this one. See tv/README.md for the
// retired identity.
const runtimeVersion = process.env.NUBARCA_TV_RUNTIME_VERSION || RELEASE_RUNTIME;
const updateChannel = process.env.NUBARCA_TV_OTA_CHANNEL || RELEASE_CHANNEL;
const codeSigningCertificate = process.env.NUBARCA_TV_OTA_CERTIFICATE;
const codeSigningCertificateConfigPath = codeSigningCertificate
  ? path.relative(__dirname, path.resolve(codeSigningCertificate))
  : null;

if (codeSigningCertificate && !fs.existsSync(codeSigningCertificate)) {
  throw new Error(`NUBARCA_TV_OTA_CERTIFICATE does not exist: ${codeSigningCertificate}`);
}
if (codeSigningCertificate) {
  validateCodeSigningCertificate(path.resolve(codeSigningCertificate));
}

if (process.env.NODE_ENV === 'production') {
  if (!releaseOrigin) {
    throw new Error(
      'NUBARCA_PUBLIC_ORIGIN is required for a production build.\n' +
        'Set it to this installation\'s public https origin in the SAME shell that\n' +
        'runs Gradle. Refusing to build a release APK without a pinned origin.',
    );
  }
  if (!releaseOrigin.startsWith('https://')) {
    throw new Error('NUBARCA_PUBLIC_ORIGIN must be an https:// origin.');
  }
  if (apiBaseUrl !== releaseOrigin) {
    throw new Error(`Production API base URL must be exactly ${releaseOrigin}.`);
  }
  if (!explicitUpdateUrl || updateUrl !== releaseUpdateUrl) {
    throw new Error(`NUBARCA_TV_OTA_UPDATE_URL is required and must be exactly ${releaseUpdateUrl}.`);
  }
  if (runtimeVersion !== RELEASE_RUNTIME) {
    throw new Error(`Production runtime must be exactly ${RELEASE_RUNTIME}.`);
  }
  if (updateChannel !== RELEASE_CHANNEL) {
    throw new Error(`Production OTA channel must be exactly ${RELEASE_CHANNEL}.`);
  }
  if (!codeSigningCertificate) {
    throw new Error('NUBARCA_TV_OTA_CERTIFICATE is required for a production build.');
  }

  const gradleProperties = readGradleProperties();
  const missingSigningInputs = RELEASE_SIGNING_INPUTS.filter(
    (name) => !(process.env[name]?.trim() || gradleProperties[name]?.trim()),
  );
  if (missingSigningInputs.length > 0) {
    throw new Error(
      `Production release signing is incomplete; missing: ${missingSigningInputs.join(', ')}.`,
    );
  }
  const storeFile = process.env.NUBARCA_TV_RELEASE_STORE_FILE || gradleProperties.NUBARCA_TV_RELEASE_STORE_FILE;
  if (!fs.existsSync(path.resolve(storeFile))) {
    throw new Error('NUBARCA_TV_RELEASE_STORE_FILE does not exist.');
  }
}

module.exports = () => ({
  expo: {
    name: 'NubArca TV',
    slug: 'nubarca-tv',
    scheme: 'nubarca-tv',
    version: RELEASE_VERSION,
    runtimeVersion,
    orientation: 'landscape',
    platforms: ['android', 'ios'],
    // Approved square launcher artwork (1024x1024), copied byte-for-byte from
    // assets/brand/nubarca/ by scripts/sync-brand-assets.py.
    icon: './assets/brand/nubarca-expo-app-icon-1024.png',
    // NOTE: there is deliberately no top-level `splash` key. Expo SDK 56 removed it
    // from the app-config schema (only `web.splash` for PWAs remains) and moved
    // splash configuration into the `expo-splash-screen` config plugin, which this
    // app does not depend on. Setting `splash` here would be silently ignored by
    // prebuild. The approved `assets/brand/nubarca-tv-splash-1920x1080.png` is
    // kept ready for the day expo-splash-screen is added, and is what the
    // in-app boot screen renders in the meantime.
    plugins: [
      // SDK 56 ships an expo-status-bar config plugin; register it explicitly
      // since this is a dynamic config (`expo install --fix` cannot auto-write it).
      'expo-status-bar',
      [
        '@react-native-tvos/config-tv',
        {
          isTV: true,
          androidTVRequired: true,
          // Android TV launcher banner slot: copied into the drawable-* resource
          // directories and referenced as android:banner in the manifest.
          // Approved Android TV banner, authored at the exact 320x180 slot —
          // never the 3:2 lockup stretched into 16:9.
          androidTVBanner: './assets/brand/nubarca-android-tv-banner-320x180.png',
        },
      ],
      // Replaces the React Native template's debug-keystore release signing with
      // the operator-supplied NubArca TV release key, and fails the build rather
      // than falling back. Must run on every prebuild, because prebuild
      // regenerates android/ from that template.
      './plugins/withReleaseSigning',
    ],
    updates: {
      url: updateUrl,
      // The application performs exactly one non-blocking check itself. Native
      // startup must always render the embedded/cached bundle immediately.
      checkAutomatically: 'NEVER',
      fallbackToCacheTimeout: 0,
      useEmbeddedUpdate: true,
      disableAntiBrickingMeasures: false,
      requestHeaders: {
        'expo-channel-name': updateChannel,
      },
      ...(codeSigningCertificate
        ? {
            // Expo resolves this field relative to the project even when given
            // an absolute-looking string, so normalize the operator path.
            codeSigningCertificate: codeSigningCertificateConfigPath,
            codeSigningMetadata: { keyid: 'main', alg: 'rsa-v1_5-sha256' },
          }
        : {}),
    },
    extra: {
      // Consumed by resolveBaseUrl() in App.tsx as the fallback when the
      // EXPO_PUBLIC_* runtime env var is absent.
      apiBaseUrl,
      otaChannel: updateChannel,
      releaseVersion: RELEASE_VERSION,
      releaseVersionCode: RELEASE_VERSION_CODE,
    },
    android: {
      // The final NubArca TV application id. It also becomes the Gradle
      // `namespace`, which prebuild derives from this value.
      //
      // NubArca (mobile, future) and NubArca TV are separate applications that
      // share one backend and account ecosystem, so `it.littlefly.nubarca` is
      // RESERVED for the mobile binary and must not be taken by this one.
      //
      // This replaced the previous TV package outright: an Android
      // applicationId has no in-place rename, so there is no upgrade path. The
      // single device holding it was uninstalled and re-paired deliberately.
      // tv/README.md names the retired package for the uninstall step.
      package: 'it.littlefly.nubarca.tv',
      versionCode: RELEASE_VERSION_CODE,
      usesCleartextTraffic,
      icon: './assets/brand/nubarca-fire-tv-icon-512.png',
      adaptiveIcon: {
        foregroundImage: './assets/brand/nubarca-expo-app-icon-1024.png',
        backgroundColor: '#0a0f1a',
      },
    },
    ios: {
      // iOS is only used for the phone-form-factor dev smoke test; allow arbitrary
      // loads there too only when the dev target is cleartext http.
      infoPlist: {
        NSAppTransportSecurity: {
          NSAllowsArbitraryLoads: usesCleartextTraffic,
        },
      },
    },
  },
});

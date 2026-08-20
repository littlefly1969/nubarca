// Dynamic Expo config for the NubArca TV app.
//
// Production has one authoritative installation value: NUBARCA_PUBLIC_ORIGIN.
// API and OTA URLs are derived from it and tv/release-contract.json. Development
// may override the API base through EXPO_PUBLIC_NUBARCA_API_BASE_URL or the
// config-only NUBARCA_TV_API_BASE_URL alias. See docs/tv-release.md for the one
// release procedure; this file deliberately does not duplicate that runbook.
//
// Cleartext (unencrypted http) traffic is enabled ONLY when the resolved base
// URL is http:// (dev on the LAN). An https:// production base URL builds with
// cleartext DISABLED — production never requires cleartext.

const DEV_DEFAULT_BASE_URL = 'http://localhost:5177';
const fs = require('node:fs');
const path = require('node:path');
const { validateCodeSigningCertificate } = require('./scripts/code-signing-certificate.cjs');
const { normalizePublicOrigin, readReleaseContract } = require('./scripts/release-contract.cjs');
const release = readReleaseContract();

// The exact origin this installation's release APK must talk to. It is supplied
// by the operator and deliberately NOT hardcoded: an installation-specific host
// is deployment configuration, not product source, and the exported repository
// must not name one.
//
// The pin it backs stays FAIL-CLOSED. A production build with the variable unset
// throws below rather than accepting whatever base URL happens to be exported —
// which is the failure mode that once produced a perfectly signed APK unable to
// reach any server.
const isProduction = process.env.NODE_ENV === 'production';
const releaseOrigin = isProduction
  ? normalizePublicOrigin(process.env.NUBARCA_PUBLIC_ORIGIN)
  : null;

const explicitBaseUrl =
  process.env.EXPO_PUBLIC_NUBARCA_API_BASE_URL || process.env.NUBARCA_TV_API_BASE_URL;

// This config is evaluated twice: once by `expo prebuild`, and again by the
// Gradle JS-bundling step that writes assets/app.config into the APK. Only the
// second decides what the shipped app talks to, so NUBARCA_PUBLIC_ORIGIN must
// remain exported in the same shell through the Gradle build. Production never
// falls back to DEV_DEFAULT_BASE_URL.
const normalizedExplicitBaseUrl = explicitBaseUrl?.replace(/\/$/, '');
if (isProduction && normalizedExplicitBaseUrl && normalizedExplicitBaseUrl !== releaseOrigin) {
  throw new Error(`Production API base URL must be exactly ${releaseOrigin}.`);
}
const apiBaseUrl = isProduction
  ? releaseOrigin
  : (normalizedExplicitBaseUrl || DEV_DEFAULT_BASE_URL);

// Only permit cleartext http on non-https (LAN dev) targets. A production
// https:// base URL does not need — and does not get — cleartext traffic.
const usesCleartextTraffic = apiBaseUrl.startsWith('http://');
const updateUrl = `${apiBaseUrl}${release.updatePath}`;
const runtimeVersion = release.runtimeVersion;
const updateChannel = release.channel;
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

if (isProduction) {
  if (!codeSigningCertificate) {
    throw new Error('NUBARCA_TV_OTA_CERTIFICATE is required for a production build.');
  }
}

module.exports = () => ({
  expo: {
    name: release.applicationName,
    slug: 'nubarca-tv',
    scheme: 'nubarca-tv',
    version: release.version,
    runtimeVersion,
    // Deliberately does NOT reach the generated manifest, and that is correct.
    // `@react-native-tvos/config-tv` runs `removePortraitOrientation`, which
    // DELETES `android:screenOrientation` from the TV launcher activity: a
    // leanback device is landscape by construction, and pinning the attribute
    // is upstream's explicit non-goal for TV builds.
    //
    // A generic-TV hardening pass added a plugin to put it back, then removed
    // it again on discovering this. Fighting the TV toolchain to restate
    // something it removes on purpose is not hardening — it is a workaround
    // against the platform, and the next clean prebuild would have silently
    // won anyway. The key is kept here because it still describes the app's
    // intent for the non-TV (iOS dev smoke) target.
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
      // Adds the NubArcaTvPlatform native module (finishAndRemoveTask), because
      // BackHandler.exitApp() only backgrounds the task on Fire TV. Same
      // prebuild-regeneration reasoning as above.
      './plugins/withTvPlatformModule',
      // Places the approved TV banner artwork in the density buckets where it
      // is actually 320x180dp, and declares android:banner on the launcher
      // ACTIVITY too. Must run AFTER @react-native-tvos/config-tv, which
      // otherwise leaves the same 320x180 bitmap in every bucket.
      './plugins/withFireTvBanner',
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
      releaseVersion: release.version,
      releaseVersionCode: release.versionCode,
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
      package: release.package,
      versionCode: release.versionCode,
      usesCleartextTraffic,
      // The two ANDROID LAUNCHER slots. Both used to point at full-bleed
      // OPAQUE squares that each contained a picture of a rounded app icon,
      // frame and halo included. Android drew exactly that: a dark rectangle
      // with a small logo in it, and — worse — masked the same opaque square as
      // the adaptive FOREGROUND, so the square always won over
      // backgroundColor. These two assets are the shapes the slots require:
      // a tile with transparent outer corners, and a transparent
      // foreground carrying only the mark inside Android's 66/108dp safe
      // square. Both are derived from the approved flat-mark master by
      // scripts/generate-android-launcher-icons.py.
      icon: './assets/brand/nubarca-android-launcher-icon-512.png',
      adaptiveIcon: {
        foregroundImage: './assets/brand/nubarca-android-adaptive-foreground-432.png',
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

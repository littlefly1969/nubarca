// Dynamic Expo config for the NubArca mobile app.
//
// Mirrors the TV app's configuration discipline (tv/app.config.js):
//   * development may target loopback / LAN over cleartext http;
//   * a PRODUCTION build (NODE_ENV=production) requires an explicit HTTPS
//     origin supplied by the operator via NUBARCA_PUBLIC_ORIGIN (with the
//     historical EXPO_PUBLIC_NUBARCA_API_BASE_URL alias still accepted) and
//     FAILS CLOSED otherwise — never a hardcoded host, never a silent
//     localhost fallback, and cleartext disabled.
//
// The variable is build-time configuration only. An installation's hostname is
// operator configuration, not product source, so it is never committed.

const DEV_DEFAULT_BASE_URL = 'http://localhost:5177';
const { normalizePublicOrigin, readReleaseContract } = require('./scripts/release-contract.cjs');
const release = readReleaseContract();

const isProduction = process.env.NODE_ENV === 'production';
const explicitBaseUrl = (
  process.env.NUBARCA_PUBLIC_ORIGIN ||
  process.env.EXPO_PUBLIC_NUBARCA_API_BASE_URL ||
  process.env.NUBARCA_MOBILE_API_BASE_URL ||
  ''
).replace(/\/$/, '');

let apiBaseUrl;
if (isProduction) {
  if (!explicitBaseUrl) {
    throw new Error(
      'Production mobile builds require NUBARCA_PUBLIC_ORIGIN (or ' +
        'EXPO_PUBLIC_NUBARCA_API_BASE_URL) ' +
        '(the installation https:// origin). Failing closed instead of ' +
        'building an app that cannot reach any server.',
    );
  }
  apiBaseUrl = normalizePublicOrigin(explicitBaseUrl);
} else {
  apiBaseUrl = explicitBaseUrl || DEV_DEFAULT_BASE_URL;
}

// Cleartext ONLY for non-https development targets. Production https builds
// ship with unrestricted cleartext traffic disabled on both platforms.
const usesCleartextTraffic = apiBaseUrl.startsWith('http://');

module.exports = {
  expo: {
    name: release.applicationName,
    slug: 'nubarca-mobile',
    version: release.version,
    orientation: 'default',
    // The app has its OWN theme (src/ui/theme.tsx) and offers `system` as one
    // of the three choices. Without 'automatic', Expo pins the native shell to
    // light and useColorScheme() answers 'light' forever — the `system` option
    // would silently be a second light option.
    userInterfaceStyle: 'automatic',
    platforms: ['ios', 'android'],
    scheme: 'nubarca',
    icon: './assets/brand/nubarca-expo-app-icon-1024.png',
    // Expo Router (SDK 54) + media-library permission plugin (mobile-sync-v1):
    // granular photo+video read access only, requested at enablement time —
    // never at startup, never for location metadata.
    plugins: [
      'expo-router',
      [
        'expo-media-library',
        {
          saveToLibrary: false,
          granularPermissions: ['photo', 'video'],
          photosPermission:
            'Allow NubArca to read the photos you choose so they can be synced to your own private library.',
          videosPermission:
            'Allow NubArca to read the videos you choose so they can be synced to your own private library.',
        },
      ],
      // BRAND-SPLASH-01. The launcher icon is deliberately NOT the splash art:
      // it is luminous and framed, and at splash scale its halo and frame read
      // as a rendering defect. The approved FLAT on-dark mark is used instead,
      // copied byte-for-byte from the brand package by scripts/sync-brand-assets.py.
      //
      // The dark variant is the SAME identity on purpose: a cold launch is
      // Midnight Navy whatever theme the user eventually gets, because the
      // stored preference is application state and is not readable by the
      // native launch (BRAND-BOOT-01).
      [
        'expo-splash-screen',
        {
          backgroundColor: '#0A0F1A',
          image: './assets/brand/nubarca-mark-flat-on-dark-256.png',
          imageWidth: 120,
          resizeMode: 'contain',
          dark: {
            backgroundColor: '#0A0F1A',
            image: './assets/brand/nubarca-mark-flat-on-dark-256.png',
          },
        },
      ],
      // `expo prebuild --clean` regenerates android/. This plugin is therefore
      // the durable release-signing authority and refuses a debug-key fallback.
      './plugins/withReleaseSigning',
    ],
    experiments: {
      typedRoutes: true,
    },
    extra: {
      apiBaseUrl,
      releaseVersion: release.version,
      releaseVersionCode: release.versionCode,
    },
    android: {
      // Reserved for the phone binary — see tv/app.config.js: the TV package
      // deliberately does NOT use this applicationId.
      package: release.package,
      versionCode: release.versionCode,
      icon: './assets/brand/nubarca-expo-app-icon-1024.png',
      adaptiveIcon: {
        foregroundImage: './assets/brand/nubarca-android-adaptive-foreground-432.png',
        backgroundColor: '#0A0F1A',
      },
      usesCleartextTraffic,
      // Privacy-explicit (release-gate review): an owner's media library and
      // account session must never ride unencrypted device backups.
      allowBackup: false,
    },
    ios: {
      bundleIdentifier: 'it.littlefly.nubarca',
      infoPlist: {
        NSAppTransportSecurity: {
          NSAllowsArbitraryLoads: usesCleartextTraffic,
        },
      },
    },
  },
};

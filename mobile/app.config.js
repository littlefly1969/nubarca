// Dynamic Expo config for the NubArca mobile app.
//
// Mirrors the TV app's configuration discipline (tv/app.config.js):
//   * development may target loopback / LAN over cleartext http;
//   * a PRODUCTION build (NODE_ENV=production) requires an explicit HTTPS
//     origin supplied by the operator via EXPO_PUBLIC_NUBARCA_API_BASE_URL and
//     FAILS CLOSED otherwise — never a hardcoded host, never a silent
//     localhost fallback, and cleartext disabled.
//
// The variable is build-time configuration only. An installation's hostname is
// operator configuration, not product source, so it is never committed.

const DEV_DEFAULT_BASE_URL = 'http://localhost:5177';

const isProduction = process.env.NODE_ENV === 'production';
const explicitBaseUrl = (
  process.env.EXPO_PUBLIC_NUBARCA_API_BASE_URL ||
  process.env.NUBARCA_MOBILE_API_BASE_URL ||
  ''
).replace(/\/$/, '');

let apiBaseUrl: string;
if (isProduction) {
  if (!explicitBaseUrl) {
    throw new Error(
      'Production mobile builds require EXPO_PUBLIC_NUBARCA_API_BASE_URL ' +
        '(the installation https:// origin). Failing closed instead of ' +
        'building an app that cannot reach any server.',
    );
  }
  if (!explicitBaseUrl.startsWith('https://')) {
    throw new Error(
      'Production mobile builds require an https:// API origin; got: ' +
        explicitBaseUrl,
    );
  }
  apiBaseUrl = explicitBaseUrl;
} else {
  apiBaseUrl = explicitBaseUrl || DEV_DEFAULT_BASE_URL;
}

// Cleartext ONLY for non-https development targets. Production https builds
// ship with unrestricted cleartext traffic disabled on both platforms.
const usesCleartextTraffic = apiBaseUrl.startsWith('http://');

module.exports = {
  expo: {
    name: 'NubArca',
    slug: 'nubarca-mobile',
    version: '0.2.0',
    orientation: 'default',
    platforms: ['ios', 'android'],
    scheme: 'nubarca',
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
    ],
    experiments: {
      typedRoutes: true,
    },
    extra: {
      apiBaseUrl,
    },
    android: {
      // Reserved for the phone binary — see tv/app.config.js: the TV package
      // deliberately does NOT use this applicationId.
      package: 'it.littlefly.nubarca',
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

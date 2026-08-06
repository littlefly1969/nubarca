// Expo config plugin: wire the NubArca TV release build to a real release key.
//
// WHY THIS EXISTS
// ---------------
// `expo prebuild` regenerates android/ from the React Native template, and that
// template ships this in app/build.gradle:
//
//     release {
//         // Caution! In production, you need to generate your own keystore file.
//         signingConfig signingConfigs.debug
//     }
//
// So every `assembleRelease` was signed with the debug keystore that ships in
// the template — the same publicly known key on every machine, with the DN
// "CN=Android Debug". The 0.2.0 APK published for the retired TV package was
// signed that way. Anyone could have produced an "update" for it.
//
// A committed edit to android/ cannot fix that, because prebuild deletes it.
// This plugin re-applies the fix on every prebuild.
//
// WHAT IT DOES
// ------------
//  1. Adds a `release` signing config fed from Gradle properties (or the
//     matching environment variables). No key material lives in this repo.
//  2. Points the release build type at it instead of at the debug config.
//  3. Fails `assembleRelease` / `bundleRelease` outright when no key is
//     configured, so a debug-signed release artifact cannot be produced by
//     accident — the build stops instead of silently shipping the template key.
//
// CONFIGURATION (operator machine only — never committed)
//
//     # ~/.gradle/gradle.properties
//     NUBARCA_TV_RELEASE_STORE_FILE=/absolute/path/to/nubarca-tv-release.jks
//     NUBARCA_TV_RELEASE_STORE_PASSWORD=...
//     NUBARCA_TV_RELEASE_KEY_ALIAS=nubarca-tv
//     NUBARCA_TV_RELEASE_KEY_PASSWORD=...
//
// Environment variables of the same names are also accepted, for CI.

const { withAppBuildGradle } = require('expo/config-plugins');

// The template text this plugin rewrites. If a future Expo/RN release changes
// the template, these anchors stop matching and the plugin THROWS rather than
// quietly leaving the debug key in place.
const DEBUG_SIGNING_CONFIG_ANCHOR = `        debug {
            storeFile file('debug.keystore')
            storePassword 'android'
            keyAlias 'androiddebugkey'
            keyPassword 'android'
        }`;

const RELEASE_BUILD_TYPE_ANCHOR = `        release {
            // Caution! In production, you need to generate your own keystore file.
            // see https://reactnative.dev/docs/signed-apk-android.
            signingConfig signingConfigs.debug`;

const RELEASE_SIGNING_CONFIG = `
        release {
            // Definitive NubArca TV release key. Supplied by the operator through
            // Gradle properties or environment variables; never stored in the
            // repository. Absent here means the gate at the bottom of this file
            // fails the build.
            def resolve = { name -> project.findProperty(name) ?: System.getenv(name) }
            def storePath = resolve('NUBARCA_TV_RELEASE_STORE_FILE')
            if (storePath) {
                storeFile file(storePath)
                storePassword resolve('NUBARCA_TV_RELEASE_STORE_PASSWORD')
                keyAlias resolve('NUBARCA_TV_RELEASE_KEY_ALIAS')
                keyPassword resolve('NUBARCA_TV_RELEASE_KEY_PASSWORD')
            }
            // minSdk is 24 (Android 7.0), so v2 alone already covers every
            // supported Fire OS and Android TV device. v1 is enabled anyway
            // because some sideload paths still inspect the JAR signature, and
            // v3 is enabled to keep in-place key rotation available later.
            enableV1Signing true
            enableV2Signing true
            enableV3Signing true
        }`;

const SIGNING_GATE = `

// Refuse to produce a release artifact signed with the template debug key.
// Without this, a missing NUBARCA_TV_RELEASE_STORE_FILE leaves the release
// signing config empty and Gradle emits an APK nobody can trust.
//
// This runs at whenReady — after the task graph is known, before any task
// executes — so a missing key fails in seconds instead of after a full release
// compile. Scoped to this project's own tasks so it cannot fire on a debug build.
gradle.taskGraph.whenReady { graph ->
    def buildsRelease = graph.allTasks.any {
        it.project == project && it.name ==~ /(assemble|bundle|package)Release/
    }
    def signing = android.signingConfigs.release
    def missingReleaseSigning = signing.storeFile == null || !signing.storeFile.exists() ||
        !signing.storePassword || !signing.keyAlias || !signing.keyPassword
    if (buildsRelease && missingReleaseSigning) {
        throw new GradleException(
            "No NubArca TV release signing key is configured.\\n" +
            "Set NUBARCA_TV_RELEASE_STORE_FILE, NUBARCA_TV_RELEASE_STORE_PASSWORD,\\n" +
            "NUBARCA_TV_RELEASE_KEY_ALIAS and NUBARCA_TV_RELEASE_KEY_PASSWORD as Gradle\\n" +
            "properties or environment variables. See docs/tv-apk-distribution.md.\\n" +
            "All four values and an existing keystore are required.\\n" +
            "Refusing to fall back to the template debug keystore."
        )
    }
}
`;

/** @type {import('expo/config-plugins').ConfigPlugin} */
const withReleaseSigning = (config) =>
  withAppBuildGradle(config, (gradleConfig) => {
    let contents = gradleConfig.modResults.contents;

    if (contents.includes('NUBARCA_TV_RELEASE_STORE_FILE')) {
      return gradleConfig; // already applied
    }
    if (!contents.includes(DEBUG_SIGNING_CONFIG_ANCHOR)) {
      throw new Error(
        'withReleaseSigning: the debug signingConfig block in app/build.gradle no ' +
          'longer matches the expected template. Refusing to continue, because ' +
          'skipping this plugin would silently produce a debug-signed release APK.',
      );
    }
    if (!contents.includes(RELEASE_BUILD_TYPE_ANCHOR)) {
      throw new Error(
        'withReleaseSigning: the release buildType in app/build.gradle no longer ' +
          'matches the expected template. Refusing to continue, because skipping ' +
          'this plugin would silently produce a debug-signed release APK.',
      );
    }

    contents = contents.replace(
      DEBUG_SIGNING_CONFIG_ANCHOR,
      DEBUG_SIGNING_CONFIG_ANCHOR + RELEASE_SIGNING_CONFIG,
    );
    contents = contents.replace(
      RELEASE_BUILD_TYPE_ANCHOR,
      `        release {
            signingConfig signingConfigs.release`,
    );

    gradleConfig.modResults.contents = contents + SIGNING_GATE;
    return gradleConfig;
  });

module.exports = withReleaseSigning;

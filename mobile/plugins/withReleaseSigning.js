// Expo config plugin: release artifacts must use the dedicated NubArca mobile
// upload key. `expo prebuild --clean` regenerates android/, so a committed
// build.gradle edit would be erased on every CI release.

const { withAppBuildGradle } = require('expo/config-plugins');

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
            def resolve = { name -> project.findProperty(name) ?: System.getenv(name) }
            def storePath = resolve('NUBARCA_MOBILE_UPLOAD_STORE_FILE')
            if (storePath) {
                storeFile file(storePath)
                storePassword resolve('NUBARCA_MOBILE_UPLOAD_STORE_PASSWORD')
                keyAlias resolve('NUBARCA_MOBILE_UPLOAD_KEY_ALIAS')
                keyPassword resolve('NUBARCA_MOBILE_UPLOAD_KEY_PASSWORD')
            }
            // v1 keeps older sideload tooling compatible; v2 and v3 cover the
            // Android install/update model used by every supported phone.
            enableV1Signing true
            enableV2Signing true
            enableV3Signing true
        }`;

const SIGNING_GATE = `

// Refuse to emit a release APK or AAB when the dedicated upload key is absent.
// The Expo template otherwise signs the release variant with its public debug
// key, producing bytes that must never establish NubArca's Android identity.
gradle.taskGraph.whenReady { graph ->
    def buildsRelease = graph.allTasks.any {
        it.project == project && it.name ==~ /(assemble|bundle|package)Release/
    }
    def signing = android.signingConfigs.release
    def missingReleaseSigning = signing.storeFile == null || !signing.storeFile.exists() ||
        !signing.storePassword || !signing.keyAlias || !signing.keyPassword
    if (buildsRelease && missingReleaseSigning) {
        throw new GradleException(
            "No NubArca mobile upload key is configured.\\n" +
            "Set NUBARCA_MOBILE_UPLOAD_STORE_FILE, NUBARCA_MOBILE_UPLOAD_STORE_PASSWORD,\\n" +
            "NUBARCA_MOBILE_UPLOAD_KEY_ALIAS and NUBARCA_MOBILE_UPLOAD_KEY_PASSWORD.\\n" +
            "See docs/mobile-release.md. Refusing the debug-keystore fallback."
        )
    }
}
`;

/** @type {import('expo/config-plugins').ConfigPlugin} */
const withReleaseSigning = (config) =>
  withAppBuildGradle(config, (gradleConfig) => {
    let contents = gradleConfig.modResults.contents;

    if (contents.includes('NUBARCA_MOBILE_UPLOAD_STORE_FILE')) {
      return gradleConfig;
    }
    if (!contents.includes(DEBUG_SIGNING_CONFIG_ANCHOR)) {
      throw new Error(
        'withReleaseSigning: the debug signingConfig block no longer matches ' +
          'the Expo template; refusing to risk an unsigned or debug-signed release.',
      );
    }
    if (!contents.includes(RELEASE_BUILD_TYPE_ANCHOR)) {
      throw new Error(
        'withReleaseSigning: the release buildType no longer matches the Expo ' +
          'template; refusing to risk a debug-signed release.',
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

# NubArca TV APK distribution

The production APK is available without authentication at the short Fire TV
Downloader URL:

`${NUBARCA_PUBLIC_ORIGIN}/tv.apk`

Its canonical URL is
`${NUBARCA_PUBLIC_ORIGIN}/download/tv/nubarca-tv.apk`.

The adjacent `nubarca-tv.apk.sha256` file contains its SHA-256 checksum. The
APK is a deployment artifact and is deliberately not committed to Git. The
frontend container mounts `${NUBARCA_TV_APK_DIR}` read-only at `/download/tv`;
nginx serves the APK with the Android package MIME type, an attachment
filename, and no-cache headers. `NUBARCA_TV_APK_DIR` is required — Compose
refuses to start the frontend when it is unset, rather than inventing a
directory that happens to match one installation.

Both routes serve the same bytes: `${NUBARCA_PUBLIC_ORIGIN}/tv.apk` is the short
form that is practical to type on a TV remote, and
`${NUBARCA_PUBLIC_ORIGIN}/download/tv/nubarca-tv.apk` is the canonical one. There
is no third route and no other filename.

## Application identity

NubArca (mobile, future) and NubArca TV are **separate applications** that share
one backend and one account ecosystem. There is no universal mobile/TV binary.

| | NubArca TV (this app) | NubArca (mobile, reserved) |
| --- | --- | --- |
| Display name | `NubArca TV` | `NubArca` |
| Android applicationId | `it.littlefly.nubarca.tv` | `it.littlefly.nubarca` |
| Android namespace | `it.littlefly.nubarca.tv` | — |
| iOS bundle identifier | — | `it.littlefly.nubarca` |
| Expo slug | `nubarca-tv` | `nubarca` |
| Deep-link scheme | `nubarca-tv` | `nubarca` |

An Android `applicationId` cannot be renamed in place. Any change to it is a
different application: there is no upgrade path, the previous app must be
uninstalled, NubArca TV installed fresh, and the device pairs again.

## Signing

The release APK is signed with the definitive NubArca TV release key. Every
future Fire TV and Android TV update must reuse the same certificate with a
higher `versionCode`; Android rejects an update signed by a different key.

The key is supplied to Gradle by the operator and is never committed. Set these
in `~/.gradle/gradle.properties` (or as environment variables of the same names,
for CI):

```properties
NUBARCA_TV_RELEASE_STORE_FILE=/absolute/path/to/nubarca-tv-release.jks
NUBARCA_TV_RELEASE_STORE_PASSWORD=…
NUBARCA_TV_RELEASE_KEY_ALIAS=nubarca-tv
NUBARCA_TV_RELEASE_KEY_PASSWORD=…
```

`tv/plugins/withReleaseSigning.js` re-applies this wiring on every prebuild,
because prebuild regenerates `android/` from the React Native template — whose
release build type is signed with the template's public debug keystore. If no
key is configured, `assembleRelease` **fails**; it never falls back to the debug
key. `deploy/publish-tv-apk.sh` independently refuses to publish an APK whose
signer DN is `CN=Android Debug`, or one without a v2/v3 signature.

Back up the keystore and its passwords in the operator's password manager.
Losing them means no further update can ever be installed over this package —
the only recovery is another applicationId change and another manual reinstall.

Record for each release: certificate SHA-256 fingerprint, signing schemes, and
where the key is held. Never record the passwords or the private key.

## Build

Native dependency or configuration changes require a new APK and runtime.

The current public artifact is **NubArca TV 1.0.1**, Android `versionCode` 2,
runtime `nubarca-tv-native-2`. It is published and is the release both canonical
routes serve. JavaScript-only changes ship as signed OTA updates under that same
runtime and do not need a new APK.

Before `expo prebuild --clean`, preserve the public Expo Updates certificate
outside `tv/android/`, because that directory is regenerated. The release
keystore already lives outside the project. Build with Node 22 and JDK 17:

```bash
cd tv
export JAVA_HOME=/usr/lib/jvm/java-17-openjdk
export ANDROID_HOME="$HOME/Android/Sdk"
export PATH="$JAVA_HOME/bin:$PATH"
export NODE_ENV=production
export EXPO_PUBLIC_NUBARCA_API_BASE_URL=https://nubarca.example.com
export NUBARCA_TV_RUNTIME_VERSION=nubarca-tv-native-2
export NUBARCA_TV_OTA_CHANNEL=production
export NUBARCA_TV_OTA_UPDATE_URL=https://nubarca.example.com/api/tv-app/updates
export NUBARCA_TV_OTA_CERTIFICATE=/absolute/path/to/expo-root.pem
npm run tv:prebuild
cd android
./gradlew assembleRelease
```

Verify the result before publication:

```bash
BT="$ANDROID_HOME/build-tools/36.0.0"
APK=app/build/outputs/apk/release/app-release.apk
"$BT/aapt2" dump badging "$APK" | grep -E "^package|^application-label:|leanback|touchscreen|sdkVersion"
"$BT/apksigner" verify --verbose --print-certs "$APK"
sha256sum "$APK"
```

Expected: package `it.littlefly.nubarca.tv`, versionCode 2, versionName 1.0.1,
label `NubArca TV`, a `leanback-launchable-activity`, `android.software.leanback`
required, touchscreen not required, v2 (and v3) verified true, and a signer DN
that is **not** `CN=Android Debug`.

## Publish

From the repository root:

```bash
./deploy/publish-tv-apk.sh \
  tv/android/app/build/outputs/apk/release/app-release.apk
```

Publication of a *new* APK is permitted only after `adb install -r` over the
currently installed release proves the package data and pairing survive. The destination is operator configuration and
has no default: `NUBARCA_PRODUCTION_SSH`, `NUBARCA_TV_APK_DIR` and
`NUBARCA_PUBLIC_ORIGIN` must all be set, and the script refuses to run
otherwise. Obtain them from the operator of the installation you are publishing
to — never infer them. The script re-verifies the
package/version/runtime/channel/endpoint, definitive signer fingerprint,
embedded OTA certificate and signatures; it then uploads to a temporary name,
atomically replaces the public APK and
checksum, and confirms the SHA-256 of the bytes that landed on the server.

Then verify headers and bytes over HTTPS:

```bash
curl -fsSI https://nubarca.example.com/tv.apk
curl -fsS  https://nubarca.example.com/download/tv/nubarca-tv.apk.sha256
curl -fsS  https://nubarca.example.com/download/tv/nubarca-tv.apk | sha256sum
```

The `Content-Type` must be `application/vnd.android.package-archive` and the
`Content-Length` must match the local file — an HTML error page served as an APK
is the failure this check exists to catch.

## Install with Fire TV Downloader

Enable **Install unknown apps** for Downloader in Fire TV settings, enter the
direct HTTPS URL above, download it, and choose **Install**.

Installing over an app with a different `applicationId` is **not** possible —
Android treats NubArca TV as a new app. Uninstall the other one first; its
pairing and session data are not carried over and the TV pairs again.

NubArca TV releases keep the same package and certificate and only raise
`versionCode`, so they install in place and preserve the pairing.

## Android TV / Google TV

The same source targets Fire TV and Android TV; there is no Fire-only tree. The
release configuration requires `android.software.leanback`, declares
touchscreen and faketouch as not required, and ships a leanback launcher
activity with an Android TV banner — the Play Store's Android TV requirements.

A Google Play AAB needs no source change: `./gradlew bundleRelease` produces
`app/build/outputs/bundle/release/app-release.aab`, signed by the same release
config and covered by the same missing-key gate. Play submission additionally
requires a Play Console entry and store listing assets, which are outside this
repository.

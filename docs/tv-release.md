# NubArca TV release runbook

This is the only operational source of truth for NubArca TV APK and OTA
releases. Installation hosts, paths and secrets are operator configuration and
must never be committed.

## 1. Current release contract

[`tv/release-contract.json`](../tv/release-contract.json) is the single tracked,
non-secret native release contract. The current release is NubArca TV 1.0.4,
package `it.littlefly.nubarca.tv`, Android `versionCode` 6, runtime
`nubarca-tv-native-5`, channel `production`. Its Android signer SHA-256 is stored
in that contract and must not change for an in-place update.

1.0.4 is a NATIVE release: its config plugin now makes Leanback manifest
generation self-contained, adding `LEANBACK_LAUNCHER` to the TV activity even
when the upstream template supplies only the ordinary launcher category. The
build-time manifest contract changed, so the runtime and Android versionCode
both incremented.

Runtime `nubarca-tv-native-5` starts with NO OTA publication, and that is
correct: the embedded bundle runs, and `/api/tv-app/updates` answers 204 for it
until a later deliberate OTA exists. Do not publish one merely to exercise the
endpoint.

A future native release intentionally updates at least `version`, `versionCode`
and `runtimeVersion` as appropriate. Every native-contract change requires a new
runtime; every in-place Android update requires a higher `versionCode`.

## 2. Decide OTA or native APK

OTA is safe only for changes compatible with the installed native runtime:
React/TypeScript UI, focus/navigation logic, business/API-client logic and
Metro-bundled images, fonts or other assets.

A new APK and runtime are required for an Expo SDK, `react-native-tvos`, native
dependency, config plugin, AndroidManifest, permission, Kotlin/Java, Gradle,
applicationId, versionCode, OTA certificate, update URL, production origin,
native channel or other build-time native configuration change. If uncertain,
classify the change as native. Never use an OTA to test native compatibility.

If an OTA depends on a new backward-compatible backend API, deploy and verify
the backend first, then publish the OTA.

## 3. Cryptographic identities

APK and OTA signing are separate:

| Boundary | Private material | Public verifier |
| --- | --- | --- |
| Android APK | release JKS and Gradle credentials, APK build host only | Android signer fingerprint in the release contract |
| Expo OTA | `TV_OTA_PRIVATE_KEY_PATH`, publisher only | `NUBARCA_TV_OTA_CERTIFICATE`, embedded in the APK and mounted into the API |

For ordinary OTA publication the APK-embedded certificate, publisher
certificate, API certificate and publisher-private-key SPKI must identify the
same established trust root. Compare full SHA-256 fingerprints over DER/SPKI,
never CN labels. Missing or different material means **stop**: do not generate a
replacement and do not rotate anything.

The OTA private key and Android keystore must never enter the API container.
Read-only is not confidentiality.

## 4. Host and container paths

These are four different concepts:

| Context | Setting | Requirement |
| --- | --- | --- |
| Publisher host | `TV_OTA_STORAGE_ROOT=<host-ota-storage>` | writable publication filesystem |
| Publisher host | `NUBARCA_TV_OTA_CERTIFICATE=<host-ota-certificate.pem>` | readable public certificate |
| Publisher host | `TV_OTA_PRIVATE_KEY_PATH=<host-ota-private-key.pem>` | private, readable only by publisher |
| API container | `TvUpdates__RootPath=/var/lib/nubarca/tv-updates` | read-only publication mount |
| API container | `TvUpdates__CodeSigningCertificatePath=/var/lib/nubarca/tv-ota-trust/certificate.pem` | read-only public-certificate file mount |

The publisher writes directly to `TV_OTA_STORAGE_ROOT`; it does not upload over
SSH. Run it on the host owning that storage, or one with the same filesystem
safely mounted read/write.

The API loads the certificate once into its singleton update store. Ordinary
OTA publication needs no container rebuild or API restart. The first certificate
mount or a changed certificate path needs one API recreate/restart. Certificate
rotation is a native transition and also needs a new APK/runtime.

## 5. OTA preflight

Prepare the publication checkout without overwriting local work:

```bash
cd <nubarca-checkout>
git fetch origin main
git switch main
git pull --ff-only origin main
test -z "$(git status --porcelain)"

cd tv
export NUBARCA_PUBLIC_ORIGIN='https://<installation-origin>'
export NUBARCA_TV_OTA_CERTIFICATE='<host-ota-certificate.pem>'
export TV_OTA_PRIVATE_KEY_PATH='<host-ota-private-key.pem>'
export TV_OTA_STORAGE_ROOT='<host-ota-storage>'
npm ci
```

Node 22.x is required. Do not source the production `.env`; set these four
values explicitly. Runtime, channel, update path and Git SHA are derived and
must not be exported.

Start with:

```bash
npm run status:ota
npm run test:ota
npm run validate:ota
```

`validate:ota` refreshes `origin/main`, requires clean `main` with
`HEAD == origin/main`, validates certificate/key, performs the production Expo
config and Android export, creates/signs/verifies a candidate entirely in a
temporary directory and removes it on success or failure. It never reads or
writes `TV_OTA_STORAGE_ROOT`, changes a pointer or exposes an update.

## 6. OTA publish

Only after preflight passes:

```bash
npm run publish:ota
npm run status:ota
npm run verify:ota
```

`publish:ota` reuses the exact preflight pipeline, then commits the immutable
publication and atomically activates its pointer. Record Git SHA, update UUID,
createdAt, runtime/channel and OTA certificate/SPKI fingerprints in the
installation release ledger. This operation does not change application or
container artifacts and does not restart services.

## 7. OTA HTTP verification

`npm run verify:ota` sends the protocol-v1 Android/runtime/channel headers,
accepts an intentional `204 No Content`, or for HTTP 200 verifies content type,
manifest identity, `Expo-Signature`, same-origin immutable asset URLs and every
asset SHA-256 hash.

## 8. Fire Stick acceptance

On first cold launch the current bundle renders and the app downloads a valid
update in the background. Wait for `[OTA] ... downloaded`, fully force-stop the
process, then cold-launch again and confirm the new update ID and visible change.
The physical Fire Stick is the authority for certificate validation, storage and
cold-launch selection.

## 9. OTA recovery and pointer rollback

For immediate containment only:

```bash
npm run rollback-pointer:ota -- <known-good-publication-uuid>
```

This changes server distribution only. It does not guarantee downgrade of a
device that already downloaded a newer valid update.

Canonical recovery is: revert/fix source on clean, pushed `main`, then publish a
**new** signed OTA with a new UUID, creation time and Git SHA under the same
compatible runtime. Complete the normal device cold-launch acceptance.

## 10. Native APK build

Do not rebuild a published APK for validation. For an intentional native
release, first update `tv/release-contract.json`, while retaining the definitive
Android signer and normally the same OTA trust root.

Required inputs are Node 22, JDK 17, Android SDK, `NUBARCA_PUBLIC_ORIGIN`,
`NUBARCA_TV_OTA_CERTIFICATE` and the four `NUBARCA_TV_RELEASE_*` Gradle
properties in the APK build machine's `~/.gradle/gradle.properties` (or its CI
environment).

```bash
cd tv
export NODE_ENV=production
export ANDROID_HOME='<android-sdk>'
export ANDROID_SDK_ROOT="$ANDROID_HOME"
export NUBARCA_PUBLIC_ORIGIN='https://<installation-origin>'
export NUBARCA_TV_OTA_CERTIFICATE='<host-ota-certificate.pem>'
npm ci --include=dev
npm run tv:prebuild
cd android
./gradlew assembleRelease
```

`--include=dev` is mandatory because Expo config plugins are build-time
devDependencies even though the generated application is a production build.
The SDK variables are explicit because the clean prebuild replaces the Android
tree, including any untracked `local.properties`.

The Gradle plugin fails `assembleRelease`, `bundleRelease` and `packageRelease`
closed if the release keystore or credentials are missing; it never falls back
to the debug signer.

## 11. APK validation

Validate an already-built/current APK locally, without remote configuration:

```bash
export NUBARCA_PUBLIC_ORIGIN='https://<installation-origin>'
export NUBARCA_TV_OTA_CERTIFICATE='<host-ota-certificate.pem>'
./deploy/validate-tv-apk.sh <apk>
```

The validator checks the whole tracked release identity, Leanback/touchscreen
contract, v2/v3 signatures, exact Android signer, embedded origin/update URL,
runtime/channel and equality of the embedded/supplied OTA certificate.

## 12. APK publication

When an authorized physical device is reachable from the build host, prefer to
run `adb install -r` before replacing the public artifact and prove that
application data and pairing survive.

When no device is reachable and the public APK is the delivery path needed to
install it, remote-first publication is allowed only after the relevant source
tests, the release build and §11 validation have all passed. Record physical
acceptance as **pending**, publish the validated bytes, and perform §13 as soon
as the APK can be installed. Never report pending acceptance as passed.

From the repository root, set operator-provided
`NUBARCA_PRODUCTION_SSH` and `NUBARCA_TV_APK_DIR` and run:

```bash
./deploy/publish-tv-apk.sh <validated-apk>
```

Publication calls the dedicated local validator first, uploads under a temporary
name, atomically replaces APK/checksum and compares the remote/local SHA-256.

## 13. Native installation acceptance

Confirm package, signer and version before installation. Use `adb install -r`
when ADB is available; after a remote-first publication, install the public APK
through the device instead. Launch and verify pairing/session persistence,
media playback and OTA cold-launch behavior on a physical device. A changed
applicationId cannot update in place and requires a fresh install/re-pair.

## 14. OTA trust rotation — do not use for normal releases

Rotation is exceptional: create a new pair only under an explicit recovery or
rotation release, choose a new runtime, build/install a new APK embedding the new
certificate, update the API certificate and restart it, then publish only under
the new runtime after device readiness. Never overwrite the established material
because a path is missing; stop and recover it.

## 15. Cleanup

Cleanup is dry-run by default:

```bash
npm run cleanup:ota
npm run cleanup:ota -- --apply --keep 5
```

Current, previous, newest retained publications and every other channel
reference are protected. Cryptographic material, the public APK/checksum and
retired keys are not temporary release noise and must never be deleted here.

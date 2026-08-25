# NubArca TV release runbook

This is the only operational source of truth for NubArca TV APK and OTA
releases. Installation hosts, paths and secrets are operator configuration and
must never be committed.

## 1. Current release contract

[`tv/release-contract.json`](../tv/release-contract.json) is the single tracked,
non-secret native release contract. The current release is NubArca TV 1.0.10,
package `it.littlefly.nubarca.tv`, Android `versionCode` 12, runtime
`nubarca-tv-native-11`, channel `production`. Its Android signer SHA-256 is stored
in that contract and must not change for an in-place update.

1.0.10 is the first release published through the immutable CI-built APK path.
The installation already retained the previously accepted, locally built
`versionCode` 11 artifact, so the CI build could not and must not replace those
different immutable bytes. This release advances the Android identity and OTA
runtime while retaining the definitive APK signer and OTA trust root.

1.0.8 corrects the Fire TV banner DENSITY contract. The Android TV banner spec
is 320x180 px **at xhdpi** — that is 160x90 dp. The plugin had read it as
"320x180 dp" and was then self-consistent with that wrong premise, placing the
320x180 asset in `drawable-mdpi` and the 1280x720 Appstore artwork in
`drawable-xxxhdpi` (1280/4 = 320). Both placements are wrong by the same factor
of two, which is why the arithmetic looked convincing. On a Fire TV, which
reports xhdpi, resource resolution took the nearest larger bucket, rescaled the
1280x720 bitmap by 2/4, and drew the banner at twice its intended dp size. The
generated tree now carries `drawable-xhdpi/tv_banner.png` and nothing else; the
1280x720 asset stays in the brand package for Appstore/promotional use and is
not an Android manifest-banner resource. Resource change, therefore native. No
artwork change, no icon change, no launcher-registration change.

1.0.7 restored `android.intent.category.LAUNCHER` beside `LEANBACK_LAUNCHER` on
the MAIN activity. An earlier plugin version removed it, guessing it was what
made Fire OS draw a square icon instead of the banner. Physical 1.0.6
acceptance disproved that twice over: the tile stayed square, AND the app
stopped appearing in the Fire TV Applications library after an ordinary
in-place update until "Move application" forced a launcher refresh. Amazon's
own Fire TV samples declare both categories. Banner declarations select TV
artwork; categories decide VISIBILITY, and the two are independent. Manifest
change, therefore a native release. No artwork and no updater change.

1.0.6 corrected the two ANDROID LAUNCHER icon slots. Both previously pointed at
opaque full-bleed squares that each contained a picture of a rounded app icon,
so the Fire TV home row drew a dark rectangle with a small logo in it, and the
adaptive foreground masked that same square instead of the mark. The legacy icon
is now a tile with transparent outer corners and the adaptive foreground is
transparent artwork inside Android's 66/108dp safe square, both derived from the
approved flat-mark master. Launcher icons are build-time resources baked into
the APK, so this cannot ship as an OTA — hence a native release. The Leanback
banner is deliberately untouched.

1.0.5 was the **in-app updater bootstrap**: it added the
`REQUEST_INSTALL_PACKAGES` permission and a PackageInstaller path to the
existing `NubArcaTvPlatform` bridge. Neither could arrive by OTA, so 1.0.5 was
the last APK that had to be installed the old way. From 1.0.6 onwards a native
release reaches the device through **Mode Select → Aggiornamenti / Updates**
with no ADB, PC or file manager — 1.0.6 is the first release to actually
exercise that path end to end.

Runtime `nubarca-tv-native-11` starts with NO OTA publication, and that is
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

An OTA never rewrites the installed Android manifest or launcher resources.
In particular it cannot add `LAUNCHER`/`LEANBACK_LAUNCHER`, replace the legacy
or adaptive application icon, replace the TV banner, or repair an installation
whose native shell predates those declarations. Those devices must install a
higher-`versionCode` APK. A fresh install and an in-place native upgrade are
therefore separate acceptance cases; seeing a launcher tile after a fresh
install does not prove an OTA-updated old APK has the same native resources.

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

### 4.1 Locating the signing material on an installation

All four publisher values are operator configuration and are never committed, so
ask the operator for them. When you have to locate them on a host you have
access to, two things make the search misleading:

- **The private key is not necessarily beside the storage root or the trust
  certificate.** It is commonly kept next to the deployment checkout under a
  `secrets/` directory that is git-ignored — often through
  `.git/info/exclude` rather than a tracked `.gitignore`, so it is invisible to
  `git status` AND absent from the repository, and a search limited to the
  storage mount will not find it. Check the checkout as well as the storage
  root, and confirm the exclusion with `git check-ignore -v secrets/`.
- **More than one keypair may be present, and the obvious one can be retired.**
  A rotation or a product rename leaves the previous pair on disk. A retired
  pair is internally consistent — its key matches its own certificate — so it
  validates against itself and looks correct right up to the point where the API
  and every device reject the signature. A directory named after an older
  runtime is a strong hint, and so is a certificate `CN` that does not match the
  current product name.

Therefore: before publishing, prove the key belongs to the trust root that is
actually in force. The API-mounted certificate is the authority, because that is
what verifies the manifest, and §3 requires the APK-embedded certificate to be
the same one.

```bash
# SPKI of the certificate the API serves against (the authority)
openssl x509 -in <host-ota-certificate.pem> -noout -pubkey \
  | openssl pkey -pubin -outform DER | sha256sum

# SPKI of the candidate private key — these two MUST be identical
openssl pkey -in <candidate-private-key.pem> -pubout -outform DER | sha256sum
```

`npm run status:ota` prints both the certificate SHA-256 and the OTA public-key
SPKI SHA-256 for the configured values, so it is the quickest confirmation once
the four variables are exported. A mismatch is §3's stop condition: do not
generate a replacement, and do not rotate.

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

### 10.1 GitHub Actions build and publication

`.github/workflows/tv-native-release.yml` is the remote equivalent of sections
10–12. It is manual-only, runs inside the `tv-production` GitHub Environment,
builds with Node 22/JDK 17/Android 36, verifies both signing identities before
compilation, runs the TV tests, builds and validates the signed APK, and uploads
the validated immutable artifact. With `publish=true` it packages those exact
three publication files in a scratch OCI image, verifies the extracted image,
and pushes it to GHCR under the immutable full-source-SHA tag. The workflow
records the manifest digest but never connects to production; activation is a
local pull from the server.

Configure these Environment **secrets**; never repository files:

| Secret | Value |
| --- | --- |
| `NUBARCA_TV_RELEASE_KEYSTORE_BASE64` | Definitive release JKS, base64 without line wrapping |
| `NUBARCA_TV_RELEASE_STORE_PASSWORD` | JKS store password |
| `NUBARCA_TV_RELEASE_KEY_ALIAS` | Definitive key alias |
| `NUBARCA_TV_RELEASE_KEY_PASSWORD` | Key password |
| `NUBARCA_TV_OTA_CERTIFICATE_BASE64` | Established public OTA certificate PEM, base64 without line wrapping |

Configure these installation-specific Environment **variables**:

| Variable | Value |
| --- | --- |
| `NUBARCA_PUBLIC_ORIGIN` | Production HTTPS origin |

The OTA private key is intentionally absent: a native APK embeds only the public
OTA verifier, and this workflow does not publish OTA bundles. Restrict the
Environment deployment branches to `main`, and protect it with required reviewers
where the GitHub plan supports them. The workflow also gates on `main` before it
materializes any signing input.

Run the workflow with `publish=false` to build and retain a validated artifact
from `main` without changing the installation. Publication is additionally
fail-closed unless `confirm_version_code` exactly matches
`tv/release-contract.json`.

The GHCR `nubarca-tv-apk` package must be readable by the production host. A
public package needs no server credential; a private package requires a
read-only `read:packages` credential. A publishing credential never belongs on
production. On the server, after pulling the matching source checkout, activate
the digest printed in the workflow summary:

```bash
./deploy/pull-publish-tv-apk-image.sh \
  --env-file .env \
  'ghcr.io/<owner>/nubarca-tv-apk@sha256:<digest>'
```

The server verifies the OCI provenance against its exact Git HEAD, extracts the
bundle, revalidates contract/hash/size, installs the immutable and canonical APK
files atomically, and replaces `nubarca-tv.release.json` last. The general
`deploy/update-production.sh check|apply` pair performs the same operation when
an APK bundle exists for the confirmed application release SHA.

Successful server publication still leaves physical Fire Stick acceptance
**pending**, as required by section 13.

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

**Publication comes before installation, and that is the normal order.** From
1.0.6 onwards the delivery path is the device's own update surface (§12.1), which
reads the published descriptor — so on an installation with no ADB access the
APK cannot reach the device until it has been published. Treating a sideload as
the preferred step would make the ordinary release look like a degraded one.

Publish once the relevant source tests, the release build and §11 validation have
all passed. Physical acceptance (§13) necessarily happens afterwards: record it as
**pending** until it is performed, and never report pending acceptance as passed.

`adb install -r` is an OPTIONAL extra check, not a prerequisite, and it exists
only where the operator has an authorized device reachable from the build host.
Many installations have no ADB access at all and never will; that is not a
missing capability to work around, and nothing in this runbook may depend on it.
Where ADB is available, installing before replacing the public artifact does add
one thing worth having: it proves application data and pairing survive an
in-place update before any device is offered the release.

The normal CI path is the server-side digest pull described in §10.1. It keeps
production unreachable from GitHub and uses no deployment key. For an APK built
and validated on an operator workstation instead, set operator-provided
`NUBARCA_PRODUCTION_SSH` and `NUBARCA_TV_APK_DIR` and run the alternate SSH
publisher from the repository root:

```bash
./deploy/publish-tv-apk.sh <validated-apk>
```

Publication calls the dedicated local validator first, then publishes in a
FAIL-CLOSED order:

1. the immutable `nubarca-tv-v<versionCode>.apk`, uploaded under a temporary
   name and moved into place;
2. a remote/local SHA-256 comparison of those bytes;
3. the canonical `nubarca-tv.apk` and `nubarca-tv.apk.sha256` — the manual
   sideload contract, unchanged;
4. `nubarca-tv.release.json` **last**.

When verifying a publication over HTTP, do not read the status code as proof of
existence. The APK directory is served by the frontend, and a frontend that
serves a single-page application answers an unknown path with **200 and the app
shell**, not 404 — so `curl -I` on an APK that was never published looks
successful, with a plausible `content-length` of a few kilobytes. The
discriminator is the content type: a real artifact answers
`application/octet-stream`, the shell answers `text/html`. Verify the SHA-256 of
the bytes, not the response code.

The release descriptor is the ACTIVATION POINTER: an installed TV reads it and
offers to install the bytes it names, so it must never be visible before those
bytes exist and have been verified on the server. `set -e` means any failure in
steps 1-3 aborts before step 4, leaving the previous descriptor intact and
devices still being offered the release that is fully published. Every field in
the descriptor is generated from `tv/release-contract.json` plus the real SHA-256
and byte count of the APK by `tv/scripts/release-descriptor.cjs`; none of it is
hand-maintained. Older versioned APKs are deliberately not cleaned up here.

## 12.1 In-app updates (from 1.0.5 onwards)

**Mode Select → Aggiornamenti / Updates** is the one update surface. It has no
PIN, holds no personal grant and calls no owner-private API.

It evaluates the native release descriptor FIRST. A published `versionCode`
higher than the installed one wins outright — an OTA belonging to the runtime the
device is leaving is neither offered nor applied. An equal `versionCode` leaves
the ordinary OTA flow in charge; a lower one is treated as stale and never
downgrades.

A native update downloads the APK from `/download/tv/<apkFile>`, composed from
the pinned production origin and a file name the client requires to equal
`nubarca-tv-v<versionCode>.apk` — the descriptor never carries a URL. Before an
install session exists, the native bridge re-verifies, against the RUNNING
install: SHA-256, that the archive parses, package name, that the candidate
`versionCode` equals the advertised one AND is strictly higher than the installed
one, and that the signing-certificate SHA-256 digests are identical. Only then is
the APK streamed into a `PackageInstaller` session committed with
`USER_ACTION_REQUIRED`; Fire OS shows its own confirmation, which the design
deliberately keeps. There is no silent, root or device-owner install path.

This changes nothing about how a release is BUILT, VALIDATED or PUBLISHED —
§10-§12 remain the procedure.

## 13. Native installation acceptance

Confirm package, signer and version before installation.

The ordinary path is the device itself: **Mode Select → Aggiornamenti / Updates**
offers the published release and installs it through Fire OS's own confirmation
(§12.1). This requires no ADB, no PC and no file manager, and it is the path
real users take — so exercising it IS the acceptance, not a substitute for one.
`adb install -r` is available only where the operator has a reachable authorized
device, and is an additional check rather than the expected route.

Then launch and verify pairing/session persistence, media playback and OTA
cold-launch behaviour on the physical device. A changed applicationId cannot
update in place and requires a fresh install and re-pair.

For launcher acceptance, test both paths when the release changes manifest or
artwork: (1) a fresh installation and (2) an in-place APK update from the
previous native `versionCode`. Confirm the tile in Fire TV Applications after
the launcher has refreshed. Do not use an OTA-only update for this check: it
cannot change the Android resources under test. Record separately whether Fire
OS presents the application-library tile as square/masked and whether it uses
the Leanback banner on TV surfaces; those are different slots.

Acceptance is performed by whoever holds the device. When that is not the agent
running §10-§12, the agent reports acceptance as **pending** and hands over the
exact steps and the expected version — it must never infer acceptance from a
successful publication.

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

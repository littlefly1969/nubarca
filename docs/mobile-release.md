# NubArca mobile Android release runbook

This is the operational source of truth for signed NubArca phone builds. The
workflow produces two artifacts from the same commit and release variant:

- `nubarca-mobile-v<versionCode>.apk` — directly installable on an Android phone;
- `nubarca-mobile-v<versionCode>.aab` — the artifact to upload to Google Play.

An AAB is not directly installable. The APK exists for physical testing before
Play Console is configured; once a Play internal track exists, that track is the
preferred acceptance path because it exercises Google's real delivery and
signing.

## 1. Permanent release identity

[`mobile/release-contract.json`](../mobile/release-contract.json) is the single
tracked, non-secret Android release contract. The initial contract is:

| Field | Value |
|---|---|
| application | NubArca |
| package / applicationId | `it.littlefly.nubarca` |
| version | `0.2.7` |
| versionCode | `8` |
| minSdk | 24 (Android 7.0) |
| targetSdk | 36 (Android 16) |
| upload signer SHA-256 | `1cfe6f9c8a52420717189e0d950196c5810608ac8e720d1d886d3248080fc06a` |

The package must never change after Play Console registration. Every new build
submitted to Play — including internal testing — must have a strictly larger
`versionCode`. `version` and `mobile/package.json` stay equal; app.config reads
the package, version and versionCode from the tracked contract.

The upload signer is deliberately different from the TV signer. Never reuse or
replace either key. The private mobile key has an operator backup outside the
repository; only its public fingerprint belongs in Git.

## 2. GitHub Environment configuration

The workflow uses the `mobile-production` GitHub Environment, restricted to
protected branches. It requires this Environment variable:

```text
NUBARCA_PUBLIC_ORIGIN=https://your-nubarca-origin.example
```

and these Environment secrets:

```text
NUBARCA_MOBILE_UPLOAD_KEYSTORE_BASE64
NUBARCA_MOBILE_UPLOAD_STORE_PASSWORD
NUBARCA_MOBILE_UPLOAD_KEY_ALIAS
NUBARCA_MOBILE_UPLOAD_KEY_PASSWORD
```

The JKS is base64-encoded as one line before storage. Neither passwords nor key
bytes may be committed, put in an Actions variable, pasted into an issue/PR, or
printed in a workflow log. Repository tests run before the workflow decodes the
key onto its ephemeral runner. The resulting APK/AAB contain only the public
certificate and the public installation origin; they contain no signing secret.

Keep the offline directory containing the JKS, recovery data, public certificate
and checksums backed up separately. Losing the upload key is recoverable through
Play after enrollment, but it breaks updates for APKs distributed directly
outside Play.

## 3. Build a phone release

1. Merge the intended code into protected `main` only after normal CI is green.
2. Read `mobile/release-contract.json` and note its exact `versionCode`.
3. Open **Actions → Mobile Android release → Run workflow**.
4. Select `main`, enter that number in `confirm_version_code`, and run it.
5. Wait for **Build and validate signed Android APK + Play AAB** to become green.
6. Download the `nubarca-mobile-<version>-vc<versionCode>` artifact from the run.

Equivalent CLI commands are:

```bash
gh workflow run mobile-android-release.yml \
  --ref main \
  -f confirm_version_code=8

gh run list --workflow mobile-android-release.yml --limit 1
gh run watch <run-id> --exit-status
gh run download <run-id> --name nubarca-mobile-0.2.7-vc8
```

The workflow fails closed unless all of these are true:

- it is running from `main` and the typed versionCode matches the contract;
- the production origin is exactly one HTTPS origin;
- typecheck, mobile tests and production-config tests pass;
- the configured keystore matches the tracked upload-certificate fingerprint;
- both `assembleRelease` and `bundleRelease` succeed with that key;
- Google `bundletool` validates the AAB and can generate a signed universal APK;
- both APK forms have the correct identity, SDK levels, origin, manifest privacy
  flags and signer;
- ARM 32/64-bit code is present, 64-bit native libraries are 16 KB page-size
  compatible, and APK zip alignment passes;
- SHA-256 checksums and GitHub build-provenance attestations are created.

The artifact is retained for 30 days. It includes the APK, AAB, public upload
certificate, `release-metadata.json`, and `SHA256SUMS`.

## 4. Install the APK on a physical Android phone

### Directly on the phone

1. Download the Actions artifact ZIP and extract it. If downloading on a PC,
   copy `nubarca-mobile-v8.apk` to the phone by USB, Nearby Share, or another
   channel you trust.
2. On Android, open the APK from **Files**.
3. When Android asks, allow **Install unknown apps** for Files (or for the one
   browser used to download it). Do not enable the global developer options.
4. Install NubArca, open it, and log in normally.
5. Disable **Install unknown apps** for that source again after installation.

Android may show the warning that the app did not come from Play; that is
expected for a signed sideload. Do not install an APK whose SHA-256 differs from
`SHA256SUMS`.

### With USB debugging / adb

After downloading and extracting the artifact:

```bash
sha256sum --check SHA256SUMS
adb devices
adb install -r nubarca-mobile-v8.apk
```

`-r` updates an existing GitHub-built NubArca install while preserving its app
data. If Android reports an incompatible signature, the phone has a debug or
differently signed build with the same package. Confirm that it is disposable,
then uninstall that old build and install the GitHub APK. Uninstalling removes
that installation's app-private data and login session; it does not delete media
stored in NubArca on the server.

All future APKs from this pipeline update the first one in place as long as the
package and upload key remain unchanged.

## 5. Move testing to Google Play

For the future public app, create the Play Console application with package
`it.littlefly.nubarca`, enroll it in **Play App Signing**, and upload the AAB to
the **Internal testing** track first. The GitHub key is the upload key: Google
verifies that signature, then signs device-specific APKs with the protected app
signing key. This separation is the recommended new-app model and lets an upload
key be reset without changing the app signing identity.

After installing NubArca from a Play track, update it through Play. A GitHub APK
signed with the upload key normally cannot replace a Play-delivered APK signed
with Google's app signing key, even though both have the same package. Keep
direct APK testing and Play-track testing as distinct installation paths.

Before each Play upload:

1. increment `versionCode` and set the intended user-facing `version` in
   `mobile/release-contract.json`;
2. set the same `version` in `mobile/package.json` and refresh the lock file;
3. merge through CI, run this workflow, and upload its `.aab` — never a local
   build — to the intended Play track;
4. complete Play Console policy declarations, data-safety answers, screenshots,
   store listing and tester access separately; the binary pipeline cannot make
   those product/legal declarations.

Do not automate Play publication until the first app, signing enrollment and
internal track have been created and accepted manually. At that point a service
account can promote the already validated AAB without changing this build
contract.

## 6. Local release builds

Local release builds are diagnostic only; GitHub remains the accepted artifact
authority. If one is necessary, keep all four `NUBARCA_MOBILE_UPLOAD_*` values
outside the repository, export `NODE_ENV=production` and
`NUBARCA_PUBLIC_ORIGIN`, run `npm run android:prebuild`, then build both Gradle
tasks. The signing plugin refuses to fall back to the Expo debug key.

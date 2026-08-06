# Native TV OTA updates

NubArca's native app in `tv/` uses `expo-updates` 56.0.23 and Expo Updates Protocol v1. The API serves only Android TV update manifests at `GET /api/tv-app/updates` and immutable files below `/api/tv-app/updates/assets/...`. These routes are anonymous and do not use owner authentication, pairing state, or `TvSession`.

OTA can replace the JavaScript/Hermes bundle and Metro-bundled assets. It cannot change the APK, Expo SDK, React Native TV, a native dependency, a config plugin, AndroidManifest, permissions, Kotlin/Java, Gradle/native build settings, or a build-time native environment value. Those changes require a new APK and a new runtime version.

## Runtime and launch behavior

`NUBARCA_TV_RUNTIME_VERSION` is a manually managed native contract. This explicit value is intentionally not derived from the application version: operators must increment it for every native/configuration change listed above, including changing native environment values embedded while building. TypeScript, React UI/layout, business logic, and bundled asset changes keep the existing runtime.

The current NubArca TV package (`it.littlefly.nubarca.tv`) uses the
`nubarca-tv-native-*` series. Release 1.0.1 uses exactly
**`nubarca-tv-native-2`**; runtime 1 belongs to the 1.0.0 APK, which did not
embed an OTA verification certificate and must always receive `204 No Content`.

A runtime version identifies one exact native ABI/configuration contract, and an update is addressed by runtime version **alone** — nothing else in the request distinguishes one application from another. So a runtime name must never be reused across application identities: publishing a NubArca TV bundle under a runtime belonging to some other package would offer it to installs of that package. Isolation is structural rather than conventional, because the publication tree and the channel pointer are both keyed by runtime (`publications/android/<runtime>/`, `channels/<channel>/android/<runtime>.json`): a device asking for a runtime with nothing published under it gets `204 No Content`. Only the `nubarca-tv-native-*` series is served.

The APK always embeds a bundle. `fallbackToCacheTimeout` is zero and the native automatic check is disabled, so the existing app renders immediately. App startup fires one background `checkForUpdateAsync`; overlapping/repeated checks in that JS process are suppressed. If an update exists it is downloaded, but `reloadAsync` is never called. It is selected only on a later cold launch. Network, HTTP, malformed manifest, signature, asset, storage, interruption, and damaged-update handling remains in the native `expo-updates` downloader/error-recovery path; failures are logged and the running or embedded update remains usable. Killing the app during a download leaves the prior complete update intact.

SDK 56's manual Updates API does not expose a configurable per-check HTTP timeout. NubArca therefore does not add a JavaScript timeout that would only abandon the promise while leaving the native request running. There is no retry loop.

Diagnostics are logged once per launch as `[TV_BOOT]` and `[OTA]`. The boot
record contains app version, versionCode, runtime, channel, update ID and
embedded/OTA state; the lifecycle record adds pending state, result and
sanitized error text. No secret is logged.

## Configuration

Build/export and server publication must use matching values:

```sh
export EXPO_PUBLIC_NUBARCA_API_BASE_URL=https://nubarca.example.com
export NUBARCA_TV_OTA_UPDATE_URL=https://nubarca.example.com/api/tv-app/updates
export NUBARCA_TV_RUNTIME_VERSION=nubarca-tv-native-2
export NUBARCA_TV_OTA_CHANNEL=production
export TV_OTA_STORAGE_ROOT=/srv/nubarca/tv-updates
export NUBARCA_TV_OTA_CERTIFICATE=/secure/nubarca-tv-ota/cert/certificate.pem
export TV_OTA_PRIVATE_KEY_PATH=/secure/nubarca-tv-ota/keys/private-key.pem
export TV_OTA_RELEASE_GIT_SHA="$(git rev-parse HEAD)"
export TV_OTA_RETENTION_COUNT=5
```

The API reads `TvUpdates__RootPath` and
`TvUpdates__CodeSigningCertificatePath`. It returns no update when the trust
certificate is absent or invalid, and cryptographically verifies the exact
manifest before serving either the manifest or an asset. Keep both locations
outside a public web root. In the required local production override, map the
publication directory and public trust certificate read-only into the API:

```yaml
# docker-compose.prod.local.yml
services:
  api:
    volumes:
      - /srv/nubarca/tv-updates:/var/lib/nubarca/tv-updates:ro
      - /secure/nubarca-tv-ota/cert/certificate.pem:/var/lib/nubarca/tv-ota-trust/certificate.pem:ro
```

Continue using the repository's overlay pattern:

```sh
docker compose -f docker-compose.prod.yml -f docker-compose.prod.local.yml --env-file .env up -d
```

## Signing and bootstrap trust

Protocol v1 code signing is supported with `rsa-v1_5-sha256`. Generate material in controlled storage; do not generate or keep the private key in this repository:

```sh
install -d -m 0700 /secure/nubarca-tv-ota/keys /secure/nubarca-tv-ota/cert
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 \
  -out /secure/nubarca-tv-ota/keys/private-key.pem
chmod 0600 /secure/nubarca-tv-ota/keys/private-key.pem
openssl req -x509 -new -sha256 \
  -key /secure/nubarca-tv-ota/keys/private-key.pem \
  -out /secure/nubarca-tv-ota/cert/certificate.pem \
  -days 1825 -subj '/CN=<your OTA trust root>' \
  -addext 'basicConstraints=critical,CA:FALSE' \
  -addext 'keyUsage=critical,digitalSignature' \
  -addext 'extendedKeyUsage=critical,codeSigning' \
  -addext 'subjectKeyIdentifier=hash' \
  -addext 'authorityKeyIdentifier=keyid:always'
chmod 0644 /secure/nubarca-tv-ota/cert/certificate.pem
openssl verify -purpose codesign \
  -CAfile /secure/nubarca-tv-ota/cert/certificate.pem \
  /secure/nubarca-tv-ota/cert/certificate.pem
```

For the bootstrap APK set
`NUBARCA_TV_OTA_CERTIFICATE=/secure/nubarca-tv-ota/cert/certificate.pem`.

## The trust contract

Three things must agree, and they are identified by **fingerprint**, never by
certificate subject or CN:

```text
OTA trust certificate embedded in the APK
  == OTA trust certificate mounted into the API
  == certificate derived from the OTA signing key used to publish
```

Verify with full SHA-256 fingerprints over DER:

```bash
# certificate identity
openssl x509 -in "$cert" -outform DER | sha256sum
# key identity: compare the certificate's public key with the signing key's
openssl x509 -in "$cert" -noout -pubkey | openssl pkey -pubin -outform DER | sha256sum
openssl pkey -in "$signing_key" -pubout -outform DER | sha256sum
```

A subject or CN is a label. It carries no authority, it is not compared by
`expo-updates`, and treating it as the contract hides exactly the failure this
check exists to catch: a certificate that looks right and verifies nothing.

If the three do not agree, publication is not safe — devices verify against the
certificate compiled into the APK they are already running.

## Rotating the OTA trust root

Rotation is a **native transition**, not a configuration change. Devices only
trust the certificate embedded in the APK they are running, so a new trust root
requires all of:

- a new native runtime version;
- a new APK embedding the new public certificate;
- retaining the matching private key under operator custody;
- a deliberate in-place device transition onto that APK.

Until a device installs the new APK it keeps verifying against the old root, so
there is no way to "switch" the trust root remotely.

This is documented so the procedure is known — **not** as something to perform.
Do not rotate unless a release explicitly calls for it.
Only
that public certificate is embedded. Publication uses
`TV_OTA_PRIVATE_KEY_PATH=/secure/nubarca-tv-ota/keys/private-key.pem`; the private
key is read locally and never served. Unsigned publication cannot be enabled:
`TV_OTA_SIGNING_REQUIRED=false` is rejected. Publication fails before export if
either key or certificate is absent or they do not match. The API independently
rejects missing, malformed or invalid signatures even when the client omits
`expo-expect-signature`.

The certificate must be currently valid, self-signed, and contain both `Key Usage: Digital Signature` and `Extended Key Usage: Code Signing`. The app configuration and publisher validate both X.509 extensions before use; this mirrors the validation performed by `expo-updates` on Android.

Back up the private key and certificate separately with restricted permissions. To rotate or recover from a compromised/expired key, create a new pair, increment the runtime, build and manually install a new APK containing the new certificate, then publish only with the new key. Existing APKs cannot trust a new root certificate over OTA. Removing signing likewise requires a new runtime and APK.

## Publish, activate, rollback, and clean up

From `tv/`, with the variables above and the private key configured, on a clean
`main` whose `HEAD` and `origin/main` both equal `TV_OTA_RELEASE_GIT_SHA`:

```sh
npm run publish:ota
```

The command accepts only runtime `nubarca-tv-native-2` and channel
`production`, runs `expo export --platform android` (never Gradle/EAS/APK
build), rejects symlinks, validates Expo metadata, copies all referenced files
into staging on the storage filesystem, computes base64url SHA-256 hashes,
records the merged Git SHA, creates a UUID update ID and creation timestamp,
writes and signs the exact manifest bytes, verifies the signature and every
reference/hash, atomically renames the immutable publication, and only then
atomically replaces the channel pointer. A failed export, verification or
signature leaves the active pointer and previous publication untouched.

Layout:

```text
tv-updates/
  publications/android/<runtime>/<update-uuid>/
    manifest.json
    publication.json
    files/...
  channels/production/android/<runtime>.json
  .staging/
```

Rollback swaps `current` and `previous` after validating the target's runtime and complete manifest:

```sh
npm run rollback:ota
```

To select an older compatible immutable ID explicitly, set `TV_OTA_ROLLBACK_TO=<uuid>` for that command. Rollback changes only the atomic pointer; it does not modify a publication. Devices that already downloaded a newer update follow Expo's monotonic selection rules, so validate rollback behavior on a real device; the server pointer mainly governs checks that have not downloaded that release.

Cleanup is dry-run by default and retains at least two newest releases plus current and immediate previous, never deleting a referenced publication:

```sh
npm run cleanup:ota
TV_OTA_CLEANUP_DRY_RUN=false TV_OTA_RETENTION_COUNT=5 npm run cleanup:ota
```

## Native APK and safe OTA test

Create each native APK using the production API URL, update URL, runtime, channel, and signing certificate above. Run `EXPO_TV=1 npm run tv:prebuild`, then the repository's normal signed Android release process. Publish the APK through the stable Downloader URL documented in [tv-apk-distribution.md](tv-apk-distribution.md). Any future native/config change needs an incremented runtime and another installed APK before updates for that runtime are published.

For a harmless end-to-end test, install the bootstrap APK, change one visible static TV label, publish with the same runtime, launch once and wait for the `[OTA] ... downloaded` diagnostic, fully force-stop the application, then launch again and confirm the label and update ID changed. Also test with the server offline, a deliberately incompatible runtime pointer, and a bad signature in a non-production test storage root; the prior UI must continue to launch.

The physical Fire Stick remains the final authority for cold-launch semantics, available storage behavior, certificate verification, and Android TV process force-stop behavior.

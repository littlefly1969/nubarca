#!/usr/bin/env bash
set -euo pipefail

# Publish an already-built NubArca TV APK without ever exposing a partial
# upload.
#
# The destination is operator configuration and is never defaulted:
#
#   NUBARCA_PRODUCTION_SSH   ssh destination of the installation
#   NUBARCA_TV_APK_DIR       directory the APK is published into
#   NUBARCA_PUBLIC_ORIGIN    https origin the published APK must talk to
#
# The canonical artifact is nubarca-tv.apk.
. "$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)/scripts/lib/operator-config.sh"
require_production_ssh
require_tv_apk_dir

apk_path="${1:-tv/android/app/build/outputs/apk/release/app-release.apk}"
target="$NUBARCA_PRODUCTION_SSH"
remote_dir="$NUBARCA_TV_APK_DIR"
remote_name="nubarca-tv.apk"
temporary_name=".${remote_name}.$$.upload"
expected_signer_sha256="d79cc09c3df0df09a279633c728d6d753e3290d74f309ced7bf73344d2ab3547"
expected_package="it.littlefly.nubarca.tv"
expected_version="1.0.1"
expected_version_code="2"
expected_runtime="nubarca-tv-native-2"
expected_channel="production"

# The origin the published APK must talk to. Operator-supplied, because an
# installation-specific host is deployment configuration and must not live in
# source. FAIL-CLOSED: an unset or non-https origin refuses the publication
# rather than skipping the check — this guard exists because a release APK once
# passed every manifest check while pointing at a LAN dev default.
require_public_origin
release_origin="$NUBARCA_PUBLIC_ORIGIN"
expected_update_url="${release_origin}/api/tv-app/updates"

if [[ ! -f "$apk_path" ]]; then
  echo "APK not found: $apk_path" >&2
  exit 1
fi

if [[ ! "$remote_dir" =~ ^/[A-Za-z0-9._/-]+$ ]]; then
  echo "Unsafe remote directory: $remote_dir" >&2
  exit 1
fi

# Refuse to publish an APK signed with the Android template debug key. The
# retired 0.2.0 TV artifact was signed that way; nubarca-tv.apk must not be.
apksigner="$(command -v apksigner || true)"
if [[ -z "$apksigner" ]]; then
  apksigner="$(ls -1 "${ANDROID_HOME:-$HOME/Android/Sdk}"/build-tools/*/apksigner 2>/dev/null | sort -V | tail -1 || true)"
fi
if [[ -z "$apksigner" ]]; then
  echo "apksigner not found; cannot verify the signature before publishing." >&2
  exit 1
fi
signer_report="$("$apksigner" verify --verbose --print-certs "$apk_path")"
if grep -q 'CN=Android Debug' <<<"$signer_report"; then
  echo "Refusing to publish: this APK is signed with the Android debug key." >&2
  exit 1
fi
signer_sha256="$(awk -F': ' '/Signer #1 certificate SHA-256 digest:/ {print tolower($2); exit}' <<<"$signer_report" | tr -d ':')"
if [[ "$signer_sha256" != "$expected_signer_sha256" ]]; then
  echo "Refusing to publish: signer certificate is not the definitive NubArca TV certificate." >&2
  exit 1
fi
if grep -q '^Signer #2 certificate' <<<"$signer_report"; then
  echo "Refusing to publish: APK must have exactly one signer." >&2
  exit 1
fi

aapt2="$(command -v aapt2 || true)"
if [[ -z "$aapt2" ]]; then
  aapt2="$(ls -1 "${ANDROID_HOME:-$HOME/Android/Sdk}"/build-tools/*/aapt2 2>/dev/null | sort -V | tail -1 || true)"
fi
apkanalyzer="$(command -v apkanalyzer || true)"
if [[ -z "$apkanalyzer" ]]; then
  apkanalyzer="$(ls -1 "${ANDROID_HOME:-$HOME/Android/Sdk}"/cmdline-tools/*/bin/apkanalyzer 2>/dev/null | sort -V | tail -1 || true)"
fi
if [[ -z "$apkanalyzer" ]]; then
  echo "apkanalyzer not found; cannot validate the embedded OTA certificate." >&2
  exit 1
fi
if [[ -z "$aapt2" ]]; then
  echo "aapt2 not found; cannot validate the Android release identity." >&2
  exit 1
fi
badging="$("$aapt2" dump badging "$apk_path")"
for required in \
  "package: name='$expected_package' versionCode='$expected_version_code' versionName='$expected_version'" \
  "application-label:'NubArca TV'" \
  "uses-feature: name='android.software.leanback'" \
  "uses-feature-not-required: name='android.hardware.touchscreen'" \
  "leanback-launchable-activity:"; do
  if ! grep -Fq "$required" <<<"$badging"; then
    echo "Refusing to publish: APK manifest check failed: $required" >&2
    exit 1
  fi
done
if ! grep -qE '^Verified using v2 scheme \(APK Signature Scheme v2\): true' <<<"$signer_report" ||
   ! grep -qE '^Verified using v3 scheme \(APK Signature Scheme v3\): true' <<<"$signer_report"; then
  echo "Refusing to publish: both APK Signature Scheme v2 and v3 are required." >&2
  exit 1
fi

# Refuse to publish an APK whose embedded config still points at a dev server.
# The manifest can be perfect — right package, label, leanback, signature — while
# the JS bundle carries the LAN fallback, because the bundle is produced by the
# Gradle step and picks up EXPO_PUBLIC_NUBARCA_API_BASE_URL separately from
# prebuild. That APK installs, launches and can never reach a server.
embedded_config="$(unzip -p "$apk_path" assets/app.config 2>/dev/null || true)"
if [[ -z "$embedded_config" ]]; then
  echo "Refusing to publish: assets/app.config is missing from the APK." >&2
  exit 1
fi
embedded_base_url="$(printf '%s' "$embedded_config" | node -e \
  'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{process.stdout.write(String(JSON.parse(s)?.extra?.apiBaseUrl??""))}catch{process.exit(1)}})')"
if [[ "$embedded_base_url" != https://* ]]; then
  echo "Refusing to publish: the embedded API base URL is not HTTPS: ${embedded_base_url:-<unset>}" >&2
  echo "Export EXPO_PUBLIC_NUBARCA_API_BASE_URL in the shell that runs Gradle." >&2
  exit 1
fi
embedded_release="$(printf '%s' "$embedded_config" | node -e \
  'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{try{const c=JSON.parse(s);process.stdout.write([c.runtimeVersion,c.updates?.requestHeaders?.["expo-channel-name"],c.updates?.url,c.version,c.android?.versionCode].join("\n"))}catch{process.exit(1)}})')"
expected_release="$(printf '%s\n' "$expected_runtime" "$expected_channel" "$expected_update_url" "$expected_version" "$expected_version_code")"
if [[ "$embedded_release" != "$expected_release" ]]; then
  echo "Refusing to publish: embedded runtime/channel/update URL/version identity is not the 1.0.1 release contract." >&2
  exit 1
fi

ota_certificate="${NUBARCA_TV_OTA_CERTIFICATE:-}"
if [[ -z "$ota_certificate" || ! -f "$ota_certificate" ]]; then
  echo "Refusing to publish: NUBARCA_TV_OTA_CERTIFICATE is required to verify embedded OTA trust." >&2
  exit 1
fi
expected_ota_cert_sha="$(openssl x509 -in "$ota_certificate" -outform DER | sha256sum | awk '{print $1}')"
certificate_temp="$(mktemp -d)"
trap 'rm -rf -- "$certificate_temp"' EXIT
decoded_manifest="$("$apkanalyzer" manifest print "$apk_path")"
if ! printf '%s' "$decoded_manifest" | node -e '
let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{
  const tag=s.match(/<meta-data\b(?=[^>]*android:name="expo\.modules\.updates\.CODE_SIGNING_CERTIFICATE")(?=[^>]*android:value="([^"]*)")[^>]*\/?\s*>/s);
  if(!tag) process.exit(1);
  process.stdout.write(tag[1].replace(/&#xA;|&#10;/gi,"\n").replace(/&quot;/g,"\"").replace(/&amp;/g,"&"));
})' > "$certificate_temp/embedded-certificate.pem"; then
  echo "Refusing to publish: OTA certificate metadata is missing from the APK." >&2
  exit 1
fi
if ! openssl x509 -in "$certificate_temp/embedded-certificate.pem" -outform DER -out "$certificate_temp/embedded-certificate.der" 2>/dev/null; then
  echo "Refusing to publish: the APK OTA certificate metadata is malformed." >&2
  exit 1
fi
embedded_ota_cert_sha="$(sha256sum "$certificate_temp/embedded-certificate.der" | awk '{print $1}')"
if [[ "$embedded_ota_cert_sha" != "$expected_ota_cert_sha" ]]; then
  echo "Refusing to publish: the configured OTA public certificate is not embedded in the APK." >&2
  exit 1
fi
echo "Embedded API base URL: $embedded_base_url"
echo "APK signer SHA-256: $signer_sha256"
echo "OTA certificate SHA-256: $expected_ota_cert_sha (embedded)"

local_sha="$(sha256sum "$apk_path" | awk '{print $1}')"
local_bytes="$(stat -c %s "$apk_path")"

if [[ "${NUBARCA_TV_APK_VALIDATE_ONLY:-false}" == "true" ]]; then
  echo "Validation only: no upload performed."
  echo "Bytes: $local_bytes"
  echo "SHA-256: $local_sha"
  exit 0
fi

ssh -F /dev/null -o BatchMode=yes "$target" "install -d -m 0755 '$remote_dir'"
scp -F /dev/null -q "$apk_path" "$target:$remote_dir/$temporary_name"
ssh -F /dev/null -o BatchMode=yes "$target" \
  "set -e; chmod 0644 '$remote_dir/$temporary_name'; mv -f '$remote_dir/$temporary_name' '$remote_dir/$remote_name'; cd '$remote_dir'; sha256sum '$remote_name' > '.${remote_name}.sha256.tmp'; chmod 0644 '.${remote_name}.sha256.tmp'; mv -f '.${remote_name}.sha256.tmp' '${remote_name}.sha256'"

# Confirm the bytes that landed are the bytes we sent, before announcing a URL.
remote_sha="$(ssh -F /dev/null -o BatchMode=yes "$target" "sha256sum '$remote_dir/$remote_name' | awk '{print \$1}'")"
if [[ "$remote_sha" != "$local_sha" ]]; then
  echo "Published bytes do not match: local $local_sha, remote $remote_sha" >&2
  exit 1
fi

echo "Published: ${release_origin}/tv.apk"
echo "Canonical: ${release_origin}/download/tv/$remote_name"
echo "Checksum:  ${release_origin}/download/tv/$remote_name.sha256"
echo "Bytes: $local_bytes"
echo "SHA-256: $local_sha (verified on the server)"

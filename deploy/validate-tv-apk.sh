#!/usr/bin/env bash
set -euo pipefail

# Validate an existing NubArca TV APK locally. This command never contacts a
# remote host and never writes outside its temporary directory.
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
. "$repository_root/scripts/lib/operator-config.sh"
require_public_origin

apk_path="${1:-$repository_root/tv/android/app/build/outputs/apk/release/app-release.apk}"
contract="$repository_root/tv/release-contract.json"
ota_certificate="${NUBARCA_TV_OTA_CERTIFICATE:-}"

if [[ ! -f "$apk_path" ]]; then
  echo "APK not found: $apk_path" >&2
  exit 1
fi
if [[ -z "$ota_certificate" || ! -f "$ota_certificate" ]]; then
  echo "NUBARCA_TV_OTA_CERTIFICATE is required to verify embedded OTA trust." >&2
  exit 1
fi

mapfile -t release_values < <(node -e '
  const { normalizePublicOrigin, readReleaseContract } = require(process.argv[1]);
  const c = readReleaseContract(process.argv[2]);
  for (const value of [c.applicationName,c.package,c.version,c.versionCode,c.runtimeVersion,c.channel,c.updatePath,c.apkSignerSha256]) console.log(value);
  console.log(normalizePublicOrigin(process.argv[3]));
' "$repository_root/tv/scripts/release-contract.cjs" "$contract" "$NUBARCA_PUBLIC_ORIGIN")
expected_name="${release_values[0]}"
expected_package="${release_values[1]}"
expected_version="${release_values[2]}"
expected_version_code="${release_values[3]}"
expected_runtime="${release_values[4]}"
expected_channel="${release_values[5]}"
expected_signer_sha256="${release_values[7]}"
NUBARCA_PUBLIC_ORIGIN="${release_values[8]}"
expected_update_url="${NUBARCA_PUBLIC_ORIGIN}${release_values[6]}"

apksigner="$(command -v apksigner || true)"
if [[ -z "$apksigner" ]]; then
  apksigner="$(find "${ANDROID_HOME:-$HOME/Android/Sdk}/build-tools" -mindepth 2 -maxdepth 2 -name apksigner -type f 2>/dev/null | sort -V | tail -1 || true)"
fi
aapt2="$(command -v aapt2 || true)"
if [[ -z "$aapt2" ]]; then
  aapt2="$(find "${ANDROID_HOME:-$HOME/Android/Sdk}/build-tools" -mindepth 2 -maxdepth 2 -name aapt2 -type f 2>/dev/null | sort -V | tail -1 || true)"
fi
apkanalyzer="$(command -v apkanalyzer || true)"
if [[ -z "$apkanalyzer" ]]; then
  apkanalyzer="$(find "${ANDROID_HOME:-$HOME/Android/Sdk}/cmdline-tools" -mindepth 3 -maxdepth 3 -path '*/bin/apkanalyzer' -type f 2>/dev/null | sort -V | tail -1 || true)"
fi
[[ -n "$apksigner" ]] || { echo "apksigner not found." >&2; exit 1; }
[[ -n "$aapt2" ]] || { echo "aapt2 not found." >&2; exit 1; }
[[ -n "$apkanalyzer" ]] || { echo "apkanalyzer not found." >&2; exit 1; }

signer_report="$("$apksigner" verify --verbose --print-certs "$apk_path")"
if grep -q 'CN=Android Debug' <<<"$signer_report"; then
  echo "APK is signed with the Android debug key." >&2
  exit 1
fi
signer_sha256="$(awk -F': ' '/Signer #1 certificate SHA-256 digest:/ {print tolower($2); exit}' <<<"$signer_report" | tr -d ':')"
if [[ "$signer_sha256" != "$expected_signer_sha256" ]]; then
  echo "APK signer is not the definitive NubArca TV signer." >&2
  echo "Expected signer SHA-256: $expected_signer_sha256" >&2
  echo "Actual signer SHA-256:   ${signer_sha256:-missing}" >&2
  exit 1
fi
if grep -q '^Signer #2 certificate' <<<"$signer_report"; then
  echo "APK must have exactly one signer." >&2
  exit 1
fi
if ! grep -qE '^Verified using v2 scheme \(APK Signature Scheme v2\): true' <<<"$signer_report" ||
   ! grep -qE '^Verified using v3 scheme \(APK Signature Scheme v3\): true' <<<"$signer_report"; then
  echo "APK Signature Schemes v2 and v3 are both required." >&2
  exit 1
fi

badging="$("$aapt2" dump badging "$apk_path")"
for required in \
  "package: name='$expected_package' versionCode='$expected_version_code' versionName='$expected_version'" \
  "application-label:'$expected_name'" \
  "uses-feature: name='android.software.leanback'" \
  "uses-feature-not-required: name='android.hardware.touchscreen'" \
  "leanback-launchable-activity:"; do
  grep -Fq "$required" <<<"$badging" || { echo "APK manifest check failed: $required" >&2; exit 1; }
done

embedded_config="$(unzip -p "$apk_path" assets/app.config 2>/dev/null || true)"
[[ -n "$embedded_config" ]] || { echo "assets/app.config is missing from the APK." >&2; exit 1; }
embedded_values="$(printf '%s' "$embedded_config" | node -e '
  let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{const c=JSON.parse(s);process.stdout.write([
    c.extra?.apiBaseUrl,c.runtimeVersion,c.updates?.requestHeaders?.["expo-channel-name"],c.updates?.url,c.version,c.android?.versionCode
  ].join("\n"))})
')"
expected_values="$(printf '%s\n' "$NUBARCA_PUBLIC_ORIGIN" "$expected_runtime" "$expected_channel" "$expected_update_url" "$expected_version" "$expected_version_code")"
[[ "$embedded_values" == "$expected_values" ]] || { echo "Embedded APK release identity/origin does not match the release contract." >&2; exit 1; }

node -e '
  const { validateCodeSigningCertificate } = require(process.argv[1]);
  validateCodeSigningCertificate(process.argv[2]);
' "$repository_root/tv/scripts/code-signing-certificate.cjs" "$ota_certificate"
expected_ota_cert_sha="$(openssl x509 -in "$ota_certificate" -outform DER | sha256sum | awk '{print $1}')"
certificate_temp="$(mktemp -d)"
trap 'rm -rf -- "$certificate_temp"' EXIT
decoded_manifest="$("$apkanalyzer" manifest print "$apk_path")"
if ! printf '%s' "$decoded_manifest" | node -e '
  let s="";process.stdin.on("data",d=>s+=d).on("end",()=>{
    const tag=s.match(/<meta-data\b(?=[^>]*android:name="expo\.modules\.updates\.CODE_SIGNING_CERTIFICATE")(?=[^>]*android:value="([^"]*)")[^>]*\/?\s*>/s);
    if(!tag) process.exit(1);
    process.stdout.write(tag[1].replace(/&#xA;|&#10;/gi,"\n").replace(/&quot;/g,"\"").replace(/&amp;/g,"&"));
  })
' > "$certificate_temp/embedded-certificate.pem"; then
  echo "OTA certificate metadata is missing from the APK." >&2
  exit 1
fi
openssl x509 -in "$certificate_temp/embedded-certificate.pem" -outform DER -out "$certificate_temp/embedded-certificate.der" 2>/dev/null || {
  echo "APK OTA certificate metadata is malformed." >&2
  exit 1
}
embedded_ota_cert_sha="$(sha256sum "$certificate_temp/embedded-certificate.der" | awk '{print $1}')"
[[ "$embedded_ota_cert_sha" == "$expected_ota_cert_sha" ]] || { echo "Supplied OTA certificate is not embedded in the APK." >&2; exit 1; }

apk_sha="$(sha256sum "$apk_path" | awk '{print $1}')"
apk_bytes="$(stat -c %s "$apk_path")"
echo "APK VALID"
echo "Bytes: $apk_bytes"
echo "SHA-256: $apk_sha"
echo "Android signer SHA-256: $signer_sha256"
echo "OTA certificate SHA-256: $expected_ota_cert_sha"
echo "Runtime: $expected_runtime"
echo "Channel: $expected_channel"

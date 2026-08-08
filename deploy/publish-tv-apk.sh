#!/usr/bin/env bash
set -euo pipefail

# Validate and atomically publish an already-built NubArca TV APK.
repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
apk_path="${1:-$repository_root/tv/android/app/build/outputs/apk/release/app-release.apk}"

# Local validation has no remote prerequisites and is the only implementation
# of APK identity, Android signature, embedded origin, and OTA trust checks.
"$repository_root/deploy/validate-tv-apk.sh" "$apk_path"

. "$repository_root/scripts/lib/operator-config.sh"
require_production_ssh
require_tv_apk_dir
require_public_origin

target="$NUBARCA_PRODUCTION_SSH"
remote_dir="$NUBARCA_TV_APK_DIR"
remote_name="nubarca-tv.apk"
temporary_name=".${remote_name}.$$.upload"
local_sha="$(sha256sum "$apk_path" | awk '{print $1}')"
local_bytes="$(stat -c %s "$apk_path")"

ssh -F /dev/null -o BatchMode=yes "$target" "install -d -m 0755 '$remote_dir'"
scp -F /dev/null -q "$apk_path" "$target:$remote_dir/$temporary_name"
ssh -F /dev/null -o BatchMode=yes "$target" \
  "set -e; chmod 0644 '$remote_dir/$temporary_name'; mv -f '$remote_dir/$temporary_name' '$remote_dir/$remote_name'; cd '$remote_dir'; sha256sum '$remote_name' > '.${remote_name}.sha256.tmp'; chmod 0644 '.${remote_name}.sha256.tmp'; mv -f '.${remote_name}.sha256.tmp' '${remote_name}.sha256'"

remote_sha="$(ssh -F /dev/null -o BatchMode=yes "$target" "sha256sum '$remote_dir/$remote_name' | awk '{print \$1}'")"
if [[ "$remote_sha" != "$local_sha" ]]; then
  echo "Published bytes do not match: local $local_sha, remote $remote_sha" >&2
  exit 1
fi

echo "Published: ${NUBARCA_PUBLIC_ORIGIN}/tv.apk"
echo "Canonical: ${NUBARCA_PUBLIC_ORIGIN}/download/tv/$remote_name"
echo "Checksum: ${NUBARCA_PUBLIC_ORIGIN}/download/tv/$remote_name.sha256"
echo "Bytes: $local_bytes"
echo "SHA-256: $local_sha (verified on the server)"

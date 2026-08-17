#!/usr/bin/env bash
set -euo pipefail

# Validate and atomically publish an already-built NubArca TV APK.
#
# Publication order is FAIL-CLOSED, and the order is the whole point:
#
#   1. upload the immutable versioned APK (nubarca-tv-v<versionCode>.apk)
#   2. verify the remote SHA-256 equals the local one
#   3. refresh the canonical nubarca-tv.apk + .sha256 (the manual sideload path)
#   4. publish nubarca-tv.release.json LAST
#
# The release descriptor is the ACTIVATION POINTER: an installed TV reads it and
# decides to install the bytes it names. It must therefore never be visible
# before those bytes exist and have been verified on the server. `set -e` means
# any failure in 1-3 aborts before step 4, leaving the PREVIOUS descriptor
# exactly as it was — devices keep being offered the release that is still
# fully published, which is the correct outcome of a failed publish.
#
# There is deliberately no cleanup of older versioned APKs here.
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
descriptor_name="nubarca-tv.release.json"
temporary_name=".${remote_name}.$$.upload"

# Every descriptor value is derived: identity from the tracked release contract,
# size and hash from the bytes actually being published. Nothing is typed twice.
descriptor_path="$(mktemp)"
trap 'rm -f -- "$descriptor_path"' EXIT
mapfile -t descriptor_values < <(
  node "$repository_root/tv/scripts/release-descriptor.cjs" "$apk_path" "$descriptor_path"
)
versioned_name="${descriptor_values[0]}"
local_sha="${descriptor_values[1]}"
local_bytes="${descriptor_values[2]}"
versioned_temporary=".${versioned_name}.$$.upload"
descriptor_temporary=".${descriptor_name}.$$.upload"

ssh -F /dev/null -o BatchMode=yes "$target" "install -d -m 0755 '$remote_dir'"

# 1. Immutable versioned APK first.
scp -F /dev/null -q "$apk_path" "$target:$remote_dir/$versioned_temporary"
ssh -F /dev/null -o BatchMode=yes "$target" \
  "set -e; chmod 0644 '$remote_dir/$versioned_temporary'; mv -f '$remote_dir/$versioned_temporary' '$remote_dir/$versioned_name'"

# 2. Prove the published bytes are the bytes described, before anything points
#    a device at them.
remote_versioned_sha="$(ssh -F /dev/null -o BatchMode=yes "$target" "sha256sum '$remote_dir/$versioned_name' | awk '{print \$1}'")"
if [[ "$remote_versioned_sha" != "$local_sha" ]]; then
  echo "Published versioned bytes do not match: local $local_sha, remote $remote_versioned_sha" >&2
  exit 1
fi

# 3. The canonical manual-download artifact keeps working exactly as before.
scp -F /dev/null -q "$apk_path" "$target:$remote_dir/$temporary_name"
ssh -F /dev/null -o BatchMode=yes "$target" \
  "set -e; chmod 0644 '$remote_dir/$temporary_name'; mv -f '$remote_dir/$temporary_name' '$remote_dir/$remote_name'; cd '$remote_dir'; sha256sum '$remote_name' > '.${remote_name}.sha256.tmp'; chmod 0644 '.${remote_name}.sha256.tmp'; mv -f '.${remote_name}.sha256.tmp' '${remote_name}.sha256'"

remote_sha="$(ssh -F /dev/null -o BatchMode=yes "$target" "sha256sum '$remote_dir/$remote_name' | awk '{print \$1}'")"
if [[ "$remote_sha" != "$local_sha" ]]; then
  echo "Published bytes do not match: local $local_sha, remote $remote_sha" >&2
  exit 1
fi

# 4. Activation LAST: only now may a device learn this release exists.
scp -F /dev/null -q "$descriptor_path" "$target:$remote_dir/$descriptor_temporary"
ssh -F /dev/null -o BatchMode=yes "$target" \
  "set -e; chmod 0644 '$remote_dir/$descriptor_temporary'; mv -f '$remote_dir/$descriptor_temporary' '$remote_dir/$descriptor_name'"

echo "Published: ${NUBARCA_PUBLIC_ORIGIN}/tv.apk"
echo "Canonical: ${NUBARCA_PUBLIC_ORIGIN}/download/tv/$remote_name"
echo "Checksum: ${NUBARCA_PUBLIC_ORIGIN}/download/tv/$remote_name.sha256"
echo "Immutable: ${NUBARCA_PUBLIC_ORIGIN}/download/tv/$versioned_name"
echo "Release descriptor: ${NUBARCA_PUBLIC_ORIGIN}/download/tv/$descriptor_name"
echo "Bytes: $local_bytes"
echo "SHA-256: $local_sha (verified on the server)"

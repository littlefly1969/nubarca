#!/usr/bin/env bash
# Start the ephemeral E2E stack and wait until it is serving.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_tool docker
require_tool curl

say "starting the ephemeral E2E stack ($E2E_PROJECT)"
mkdir -p "$STATE_DIR" "$ARTIFACT_DIR"

# Build when the SOURCE has changed, not merely when the image is missing.
#
# "Only if missing" is what let a gate run against images that predated the
# commit it claimed to certify: the images existed, so nothing rebuilt, and the
# suite reported a confident pass for code it never executed. Comparing the
# stamp instead keeps the fast path for an unchanged tree while making a stale
# image impossible to run against by accident.
E2E_SOURCE_FINGERPRINT="$(e2e_source_fingerprint)"
export E2E_SOURCE_FINGERPRINT

for image in nubarca-e2e-api:local nubarca-e2e-web:local; do
  if [ "$(e2e_image_fingerprint "$image")" != "$E2E_SOURCE_FINGERPRINT" ]; then
    info "building images (source fingerprint $E2E_SOURCE_FINGERPRINT)"
    dc build
    break
  fi
done

dc up -d --wait postgres 2>&1 | sed 's/^/   /' || {
  dc logs --tail 40 postgres | sed 's/^/   /' >&2
  die "the E2E database failed to start"
}
dc up -d api worker web 2>&1 | sed 's/^/   /' || {
  dc logs --tail 40 api worker web | sed 's/^/   /' >&2
  die "the E2E stack failed to start"
}

wait_for_url "$E2E_API_URL/health" "API" 180
wait_for_url "$E2E_WEB_URL/" "web front door" 120

# Belt and braces: prove the containers that are now serving were built from
# this working tree. The check reads the image each container was CREATED from,
# so it also catches a rebuild the stack never picked up.
assert_images_match_source "$E2E_SOURCE_FINGERPRINT"

info "stack up: $(dc ps --services | tr '\n' ' ')"

#!/usr/bin/env bash
# Start the ephemeral E2E stack and wait until it is serving.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_tool docker
require_tool curl

say "starting the ephemeral E2E stack ($E2E_PROJECT)"
mkdir -p "$STATE_DIR" "$ARTIFACT_DIR"

# Build only if the image is missing; an explicit rebuild is `dc build`.
for image in nubarca-e2e-api:local nubarca-e2e-web:local; do
  if ! docker image inspect "$image" >/dev/null 2>&1; then
    info "building images (first run)"
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
info "stack up: $(dc ps --services | tr '\n' ' ')"

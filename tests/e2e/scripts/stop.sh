#!/usr/bin/env bash
# Tear the ephemeral stack down, including its volumes.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

assert_own_project

say "stopping the E2E stack ($E2E_PROJECT)"
# -v is safe here and only here: every volume in docker-compose.e2e.yml is
# anonymous and belongs to this project. assert_own_project above is what makes
# that guarantee, so never remove it.
dc down -v --remove-orphans 2>&1 | sed 's/^/   /' || true

if [ -n "${E2E_KEEP_STATE:-}" ]; then
  info "keeping $STATE_DIR (E2E_KEEP_STATE set)"
else
  rm -rf "$STATE_DIR"
fi

remaining="$(docker ps -a --filter "label=com.docker.compose.project=$E2E_PROJECT" --format '{{.Names}}')"
volumes="$(docker volume ls --filter "label=com.docker.compose.project=$E2E_PROJECT" --format '{{.Name}}')"
info "containers remaining: ${remaining:-none}"
info "volumes remaining:    ${volumes:-none}"
[ -z "$remaining" ] || die "E2E containers survived teardown: $remaining"
[ -z "$volumes" ] || die "E2E volumes survived teardown: $volumes"

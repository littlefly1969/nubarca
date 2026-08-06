#!/usr/bin/env bash
# Shared plumbing for the E2E lifecycle scripts.
set -euo pipefail

E2E_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$E2E_ROOT/../.." && pwd)"

# The Compose project this suite owns. Every destructive command asserts against
# this exact name: `down -v` deletes volumes, and pointing it at the development
# or production project would destroy real data.
E2E_PROJECT="nubarca-e2e"
COMPOSE_FILE="$E2E_ROOT/docker-compose.e2e.yml"

E2E_API_PORT="${E2E_API_PORT:-5277}"
E2E_WEB_PORT="${E2E_WEB_PORT:-5273}"
E2E_API_URL="${E2E_API_URL:-http://127.0.0.1:${E2E_API_PORT}}"
E2E_WEB_URL="${E2E_WEB_URL:-http://127.0.0.1:${E2E_WEB_PORT}}"
export E2E_API_PORT E2E_WEB_PORT E2E_API_URL E2E_WEB_URL

STATE_DIR="$E2E_ROOT/.state"
ARTIFACT_DIR="$E2E_ROOT/.artifacts"

say() { printf '\n\033[1m== %s\033[0m\n' "$1"; }
info() { printf '   %s\n' "$1"; }
die() { printf '\n\033[31mE2E: %s\033[0m\n' "$1" >&2; exit 1; }

# Every compose invocation goes through this, so the project name can never be
# omitted by accident.
dc() {
  docker compose --project-name "$E2E_PROJECT" -f "$COMPOSE_FILE" "$@"
}

# Refuse to run a destructive compose command against anything but our project.
assert_own_project() {
  [ "$E2E_PROJECT" = "nubarca-e2e" ] \
    || die "refusing to run a destructive command against project '$E2E_PROJECT'"
}

require_tool() {
  command -v "$1" >/dev/null 2>&1 || die "$1 is required but not on PATH${2:+ ($2)}"
}

wait_for_url() {
  local url="$1" what="$2" budget="${3:-120}"
  local waited=0
  until curl -fsS -o /dev/null --max-time 3 "$url" 2>/dev/null; do
    waited=$((waited + 2))
    [ "$waited" -lt "$budget" ] || die "$what did not become ready within ${budget}s ($url)"
    sleep 2
  done
  info "$what is ready ($url)"
}

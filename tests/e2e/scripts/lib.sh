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

# ---------------------------------------------------------------------------
# Image provenance
#
# The gate builds the product into images and then drives a browser against
# them. If those images predate the source under test, the run still passes and
# still reads as "131/131 against this commit" — a FALSE GREEN, and the worst
# kind, because the report is confident and wrong.
#
# So every image is stamped at build time with a fingerprint of the source that
# went into it, and the authoritative run refuses to report a result unless the
# RUNNING containers carry the fingerprint of the working tree being tested.
#
# The fingerprint is over the working tree, not `git rev-parse HEAD`: the gate is
# meant to be run BEFORE committing, so a commit-based stamp would either reject
# every pre-commit run or quietly ignore uncommitted edits — which is the exact
# hole this closes.
E2E_SOURCE_LABEL="nubarca.e2e.source-fingerprint"

# Content hash of everything that lands in the two build contexts. Tracked files
# plus untracked-but-not-ignored ones, so a brand-new file counts; ignored paths
# (node_modules, dist, bin, obj) are excluded by construction, which is also what
# keeps this fast.
e2e_source_fingerprint() {
  (
    cd "$REPO_ROOT"
    {
      git ls-files -z -- src frontend
      git ls-files -z --others --exclude-standard -- src frontend
    } | sort -z | xargs -0 -r sha256sum
    # The compose file decides how those contexts are built, so a change to it
    # is a change to the images.
    sha256sum "$COMPOSE_FILE"
  ) | sha256sum | cut -c1-16
}

# The fingerprint an image was built with, or empty when it carries none (an
# image built before this stamp existed, or by a plain `docker build`).
e2e_image_fingerprint() {
  docker image inspect "$1" \
    --format "{{index .Config.Labels \"$E2E_SOURCE_LABEL\"}}" 2>/dev/null || true
}

# The authoritative provenance check. Reads the fingerprint off the image each
# RUNNING container was actually created from — not off the tag, which can be
# moved after the fact — so it cannot be satisfied by a rebuild that the stack
# never picked up.
assert_images_match_source() {
  local want="$1" service cid image got
  for service in api worker web; do
    cid="$(dc ps -q "$service" 2>/dev/null || true)"
    [ -n "$cid" ] || die "provenance: the '$service' container is not running"
    image="$(docker inspect "$cid" --format '{{.Image}}')"
    got="$(e2e_image_fingerprint "$image")"
    if [ "$got" != "$want" ]; then
      die "provenance: '$service' is running an image built from ${got:-an unstamped source} but the working tree is $want.
      The result of this run would describe code that is not the code under test.
      Rebuild with: (cd tests/e2e && docker compose --project-name $E2E_PROJECT -f docker-compose.e2e.yml build)"
    fi
  done
  info "images match the source under test ($want)"
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

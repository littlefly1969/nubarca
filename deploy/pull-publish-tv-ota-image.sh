#!/usr/bin/env bash
# Pull a GitHub-built, signed TV OTA bundle by GHCR digest and activate it.
# This server-side command never builds and never receives a private key.

set -Eeuo pipefail

log() { printf '[tv-ota-pull] %s\n' "$*"; }
fail() { printf '[tv-ota-pull] error: %s\n' "$*" >&2; exit 1; }

usage() {
    printf 'usage: %s --env-file <production.env> <image@sha256:digest>\n' "$0" >&2
    exit 2
}

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE=""
if [[ "${1:-}" == "--env-file" ]]; then
    [[ $# -ge 3 ]] || usage
    ENV_FILE="$2"
    shift 2
fi
[[ $# -eq 1 && -n "$ENV_FILE" ]] || usage
IMAGE="$1"
[[ "$IMAGE" =~ ^ghcr\.io/[a-z0-9._-]+/nubarca-tv-ota@sha256:[0-9a-f]{64}$ ]] || \
    fail "image must be ghcr.io/<owner>/nubarca-tv-ota@sha256:<64 lowercase hex>"

cd "$REPO_ROOT"
[[ -f "$ENV_FILE" ]] || fail "environment file not found: $ENV_FILE"
command -v docker >/dev/null || fail "docker is required"

read_env_value() {
    local name="$1" line value
    line="$(sed -n -E "s/^[[:space:]]*(export[[:space:]]+)?${name}=//p" "$ENV_FILE" | tail -n 1)"
    [[ -n "$line" ]] || return 1
    value="$line"
    if [[ "$value" == \"*\" && "$value" == *\" ]]; then
        value="${value:1:${#value}-2}"
    elif [[ "$value" == \'*\' && "$value" == *\' ]]; then
        value="${value:1:${#value}-2}"
    fi
    printf '%s' "$value"
}

PUBLIC_ORIGIN="$(read_env_value NUBARCA_PUBLIC_ORIGIN || true)"
STORAGE_ROOT="$(read_env_value NUBARCA_TV_OTA_STORAGE_ROOT || true)"
CERTIFICATE="$(read_env_value NUBARCA_TV_OTA_CERTIFICATE || true)"
NODE_BIN="$(read_env_value NUBARCA_TV_NODE || true)"
[[ "$PUBLIC_ORIGIN" =~ ^https://[^/]+/?$ ]] || fail "NUBARCA_PUBLIC_ORIGIN must be an HTTPS origin"
for value in "$STORAGE_ROOT" "$CERTIFICATE" "$NODE_BIN"; do
    [[ "$value" == /* && "$value" != "/" ]] || fail "TV OTA paths in the environment file must be absolute non-root paths"
done
[[ -d "$STORAGE_ROOT" ]] || fail "NUBARCA_TV_OTA_STORAGE_ROOT is unavailable"
[[ -f "$CERTIFICATE" ]] || fail "NUBARCA_TV_OTA_CERTIFICATE is unavailable"
[[ -x "$NODE_BIN" ]] || fail "NUBARCA_TV_NODE is not executable"

[[ "$(git branch --show-current)" == "main" ]] || fail "production checkout must be on main"
[[ -z "$(git status --porcelain)" ]] || fail "production checkout is dirty"
head_sha="$(git rev-parse HEAD)"
origin_sha="$(git rev-parse origin/main)"
[[ "$head_sha" == "$origin_sha" ]] || fail "HEAD must equal the already-fetched origin/main"

log "pulling immutable bundle $IMAGE"
docker pull "$IMAGE" >/dev/null
artifact_label="$(docker image inspect --format '{{ index .Config.Labels "io.nubarca.artifact" }}' "$IMAGE")"
source_sha="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$IMAGE")"
runtime_label="$(docker image inspect --format '{{ index .Config.Labels "io.nubarca.tv.runtime-version" }}' "$IMAGE")"
expected_runtime="$("$NODE_BIN" -p "require('./tv/release-contract.json').runtimeVersion")"
[[ "$artifact_label" == "tv-ota" ]] || fail "image is not a NubArca TV OTA bundle"
[[ "$source_sha" == "$head_sha" ]] || fail "bundle source $source_sha does not match checkout $head_sha"
[[ "$runtime_label" == "$expected_runtime" ]] || fail "bundle runtime does not match the checkout release contract"

bundle_dir="$(mktemp -d /tmp/nubarca-tv-ota-bundle.XXXXXXXX)"
container=""
cleanup() {
    if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
    if [[ "$bundle_dir" == /tmp/nubarca-tv-ota-bundle.* && -d "$bundle_dir" ]]; then
        rm -rf -- "$bundle_dir"
    fi
}
trap cleanup EXIT

container="$(docker create "$IMAGE" /bin/true)"
docker cp "$container:/release/." "$bundle_dir"
docker rm -f "$container" >/dev/null
container=""

export NUBARCA_PUBLIC_ORIGIN="$PUBLIC_ORIGIN"
export NUBARCA_TV_OTA_CERTIFICATE="$CERTIFICATE"
export TV_OTA_STORAGE_ROOT="$STORAGE_ROOT"
unset TV_OTA_PRIVATE_KEY_PATH
"$NODE_BIN" tv/scripts/ota.mjs import-bundle "$bundle_dir" "$head_sha"
log "activated signed OTA bundle source=$head_sha runtime=$expected_runtime"

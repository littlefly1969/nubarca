#!/usr/bin/env bash
# Pull a CI-validated TV APK bundle by immutable GHCR digest and activate it.

set -Eeuo pipefail

log() { printf '[tv-apk-pull] %s\n' "$*"; }
fail() { printf '[tv-apk-pull] error: %s\n' "$*" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ENV_FILE=""

if [[ "${1:-}" == "--env-file" ]]; then
    [[ $# -ge 3 ]] || fail "usage: $0 --env-file <production.env> <image@sha256:digest>"
    ENV_FILE="$2"
    shift 2
fi
[[ $# -eq 1 ]] || fail "usage: $0 --env-file <production.env> <image@sha256:digest>"
IMAGE="$1"
[[ -n "$ENV_FILE" ]] || fail "--env-file is required"
[[ "$IMAGE" =~ ^ghcr\.io/[a-z0-9._-]+/nubarca-tv-apk@sha256:[0-9a-f]{64}$ ]] || \
    fail "image must be ghcr.io/<owner>/nubarca-tv-apk@sha256:<64 lowercase hex>"

cd "$REPO_ROOT"
[[ -f "$ENV_FILE" ]] || fail "environment file not found: $ENV_FILE"
command -v docker >/dev/null || fail "docker is required"
command -v python3 >/dev/null || fail "python3 is required"

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
APK_DIR="$(read_env_value NUBARCA_TV_APK_DIR || true)"
[[ "$PUBLIC_ORIGIN" =~ ^https://[^/]+/?$ ]] || fail "NUBARCA_PUBLIC_ORIGIN must be an HTTPS origin"
[[ "$APK_DIR" == /* && "$APK_DIR" != "/" ]] || fail "NUBARCA_TV_APK_DIR must be an absolute non-root path"

[[ "$(git branch --show-current)" == "main" ]] || fail "production checkout must be on main"
[[ -z "$(git status --porcelain)" ]] || fail "production checkout is dirty"
head_sha="$(git rev-parse HEAD)"
origin_sha="$(git rev-parse origin/main)"
[[ "$head_sha" == "$origin_sha" ]] || fail "HEAD must equal the already-fetched origin/main"

log "pulling immutable bundle $IMAGE"
docker pull "$IMAGE" >/dev/null
artifact_label="$(docker image inspect --format '{{ index .Config.Labels "io.nubarca.artifact" }}' "$IMAGE")"
source_sha="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$IMAGE")"
[[ "$artifact_label" == "tv-apk" ]] || fail "image is not a NubArca TV APK bundle"
[[ "$source_sha" == "$head_sha" ]] || fail "bundle source $source_sha does not match checkout $head_sha"

bundle_dir="$(mktemp -d /tmp/nubarca-tv-apk-bundle.XXXXXXXX)"
container=""
cleanup() {
    if [[ -n "$container" ]]; then
        docker rm -f "$container" >/dev/null 2>&1 || true
    fi
    if [[ "$bundle_dir" == /tmp/nubarca-tv-apk-bundle.* && -d "$bundle_dir" ]]; then
        rm -rf -- "$bundle_dir"
    fi
}
trap cleanup EXIT

container="$(docker create "$IMAGE" /bin/true)"
docker cp "$container:/release/." "$bundle_dir"
docker rm -f "$container" >/dev/null
container=""

values="$(python3 deploy/validate-tv-apk-bundle.py "$bundle_dir" tv/release-contract.json --values)"
IFS=$'\t' read -r apk_name apk_sha apk_bytes version_code <<< "$values"
[[ -n "$apk_name" && -n "$apk_sha" && -n "$apk_bytes" && -n "$version_code" ]] || \
    fail "bundle validator did not return publication values"

install -d -m 0755 "$APK_DIR"
immutable_target="$APK_DIR/$apk_name"
if [[ -e "$immutable_target" ]]; then
    existing_sha="$(sha256sum "$immutable_target" | awk '{print $1}')"
    [[ "$existing_sha" == "$apk_sha" ]] || fail "immutable target exists with different bytes: $immutable_target"
else
    install -m 0644 "$bundle_dir/$apk_name" "$immutable_target.tmp.$$"
    mv -n "$immutable_target.tmp.$$" "$immutable_target"
fi
[[ "$(sha256sum "$immutable_target" | awk '{print $1}')" == "$apk_sha" ]] || \
    fail "published immutable APK failed its hash check"

# The descriptor is the activation pointer, therefore it is replaced last.
install -m 0644 "$bundle_dir/$apk_name" "$APK_DIR/nubarca-tv.apk.tmp.$$"
mv -f "$APK_DIR/nubarca-tv.apk.tmp.$$" "$APK_DIR/nubarca-tv.apk"
install -m 0644 "$bundle_dir/$apk_name.sha256" "$APK_DIR/nubarca-tv.apk.sha256.tmp.$$"
mv -f "$APK_DIR/nubarca-tv.apk.sha256.tmp.$$" "$APK_DIR/nubarca-tv.apk.sha256"
install -m 0644 "$bundle_dir/nubarca-tv.release.json" "$APK_DIR/nubarca-tv.release.json.tmp.$$"
mv -f "$APK_DIR/nubarca-tv.release.json.tmp.$$" "$APK_DIR/nubarca-tv.release.json"

printf '%s\n' "$source_sha" > "$APK_DIR/.nubarca-tv.source.tmp.$$"
mv -f "$APK_DIR/.nubarca-tv.source.tmp.$$" "$APK_DIR/.nubarca-tv.source"

log "activated TV APK versionCode=$version_code bytes=$apk_bytes sha256=$apk_sha"
log "descriptor: ${PUBLIC_ORIGIN%/}/download/tv/nubarca-tv.release.json"
log "APK: ${PUBLIC_ORIGIN%/}/download/tv/$apk_name"

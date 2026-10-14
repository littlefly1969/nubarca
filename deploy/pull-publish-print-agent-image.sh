#!/usr/bin/env bash
# Pull a CI-built Print Agent bundle by immutable GHCR digest and publish it for
# the printers page: the zip, then — last, as the activation pointer — its
# current-<runtime>.json, into the API's read-only volume nubarca-print-agent-data.
#
# Trust comes from WHERE the image lives, not from what it says about itself:
# only NubArca's own GHCR repository is accepted (deploy/print-agent-bundle.py,
# TRUSTED_REPOSITORY), by immutable digest. Inside it, the manifest, labels and
# zip must agree, and the source commit must be on this checkout's main. The API
# must already mount the volume: deploy the release that adds it before the
# first publication. Whether the page hands the bundle out is a separate switch,
# set by an administrator on the printers page.

set -Eeuo pipefail

log() { printf '[print-agent-pull] %s\n' "$*"; }
fail() { printf '[print-agent-pull] error: %s\n' "$*" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VOLUME="nubarca-print-agent-data"

[[ $# -eq 1 ]] || fail "usage: $0 <trusted repository>@sha256:<digest>"

cd "$REPO_ROOT"
command -v docker >/dev/null || fail "docker is required"
command -v python3 >/dev/null || fail "python3 is required"
VALIDATOR="$REPO_ROOT/deploy/print-agent-bundle.py"
# Checked before anything is pulled: an image from any other repository is refused.
IMAGE="$(python3 "$VALIDATOR" image "$1")" || fail "untrusted or malformed image reference"
[[ "$(git branch --show-current)" == "main" ]] || fail "production checkout must be on main"
[[ -z "$(git status --porcelain)" ]] || fail "production checkout is dirty"
[[ "$(git rev-parse HEAD)" == "$(git rev-parse origin/main)" ]] || fail "HEAD must equal the already-fetched origin/main"

docker inspect nubarca-api --format '{{range .Mounts}}{{.Name}} {{end}}' | tr ' ' '\n' | grep -qx "$VOLUME" || \
    fail "nubarca-api does not mount $VOLUME: deploy the release that adds it first"
api_image="$(docker inspect nubarca-api --format '{{.Image}}')"

log "pulling immutable bundle $IMAGE"
docker pull "$IMAGE" >/dev/null
artifact_label="$(docker image inspect --format '{{ index .Config.Labels "io.nubarca.artifact" }}' "$IMAGE")"
source_sha="$(docker image inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' "$IMAGE")"

bundle_dir="$(mktemp -d /tmp/nubarca-print-agent-bundle.XXXXXXXX)"
container=""
cleanup() {
    if [[ -n "$container" ]]; then docker rm -f "$container" >/dev/null 2>&1 || true; fi
    if [[ "$bundle_dir" == /tmp/nubarca-print-agent-bundle.* && -d "$bundle_dir" ]]; then rm -rf -- "$bundle_dir"; fi
}
trap cleanup EXIT

container="$(docker create "$IMAGE" /bin/true)"
docker cp "$container:/release/." "$bundle_dir"
docker rm -f "$container" >/dev/null
container=""

# The pointer names exactly one zip, of exactly these bytes, from a commit on main.
values="$(python3 "$VALIDATOR" validate "$bundle_dir" "$artifact_label" "$source_sha" "$REPO_ROOT")" || \
    fail "bundle validation failed"
IFS=$'\t' read -r zip_name zip_sha version zip_bytes <<< "$values"

# The volume belongs to Docker: a throwaway container of the API's own image
# writes it, so neither sudo nor another image is needed. Immutable zip first;
# the pointer is replaced last, so the page never names a file not yet there.
docker run --rm --network none --user 0:0 --entrypoint /bin/sh \
    -e ZIP_NAME="$zip_name" -e ZIP_SHA="$zip_sha" \
    -v "$VOLUME:/out" -v "$bundle_dir:/in:ro" "$api_image" -c '
set -eu
if [ -e "/out/$ZIP_NAME" ]; then
  [ "$(sha256sum "/out/$ZIP_NAME" | cut -d" " -f1)" = "$ZIP_SHA" ] || { echo "immutable target exists with other bytes" >&2; exit 1; }
else
  cp "/in/$ZIP_NAME" "/out/$ZIP_NAME.tmp"
  chmod 0644 "/out/$ZIP_NAME.tmp"
  mv "/out/$ZIP_NAME.tmp" "/out/$ZIP_NAME"
fi
[ "$(sha256sum "/out/$ZIP_NAME" | cut -d" " -f1)" = "$ZIP_SHA" ] || { echo "published zip failed its hash check" >&2; exit 1; }
cp /in/nubarca-print-agent.release.json /out/current-linux-x64.json.tmp
chmod 0644 /out/current-linux-x64.json.tmp
mv -f /out/current-linux-x64.json.tmp /out/current-linux-x64.json
' || fail "publication into $VOLUME failed"

log "published Print Agent $version ($zip_bytes bytes, sha256=$zip_sha) from $source_sha"
log "an administrator switches the download on from the printers page"

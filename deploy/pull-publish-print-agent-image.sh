#!/usr/bin/env bash
# Pull a CI-built Print Agent bundle by immutable GHCR digest and publish it for
# the printers page: the zip, then — last, as the activation pointer — its
# current-<runtime>.json, into the API's read-only volume nubarca-print-agent-data.
#
# The bundle must be built from this checkout's main (its commit an ancestor of
# HEAD), and the API must already mount the volume: deploy the release that adds
# it before the first publication. Whether the page hands the bundle out is a
# separate switch, set by an administrator on the printers page.

set -Eeuo pipefail

log() { printf '[print-agent-pull] %s\n' "$*"; }
fail() { printf '[print-agent-pull] error: %s\n' "$*" >&2; exit 1; }

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VOLUME="nubarca-print-agent-data"

[[ $# -eq 1 ]] || fail "usage: $0 <ghcr.io/<owner>/nubarca-print-agent@sha256:digest>"
IMAGE="$1"
[[ "$IMAGE" =~ ^ghcr\.io/[a-z0-9._-]+/nubarca-print-agent@sha256:[0-9a-f]{64}$ ]] || \
    fail "image must be ghcr.io/<owner>/nubarca-print-agent@sha256:<64 lowercase hex>"

cd "$REPO_ROOT"
command -v docker >/dev/null || fail "docker is required"
command -v python3 >/dev/null || fail "python3 is required"
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
[[ "$artifact_label" == "print-agent" ]] || fail "image is not a NubArca Print Agent bundle"
[[ "$source_sha" =~ ^[0-9a-f]{40}$ ]] || fail "bundle carries no source commit"
git merge-base --is-ancestor "$source_sha" HEAD || fail "bundle source $source_sha is not on this checkout's main"

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

# The pointer names exactly one zip, of exactly these bytes, from this commit.
values="$(python3 - "$bundle_dir" "$source_sha" <<'PY'
import hashlib, json, os, re, sys
root, source = sys.argv[1], sys.argv[2]
manifest = json.load(open(os.path.join(root, "nubarca-print-agent.release.json"), encoding="utf-8"))
version, runtime = manifest.get("version", ""), manifest.get("runtime", "")
name, digest, size = manifest.get("fileName", ""), manifest.get("sha256", ""), manifest.get("sizeBytes")
if manifest.get("artifact") != "print-agent": sys.exit("manifest is not a Print Agent bundle")
if not re.fullmatch(r"\d{1,4}\.\d{1,4}\.\d{1,4}", version): sys.exit("invalid version")
if runtime != "linux-x64": sys.exit("only linux-x64 is published")
if name != f"nubarca-print-agent-{version}-{runtime}.zip": sys.exit("unexpected file name")
if not re.fullmatch(r"[0-9a-f]{64}", digest): sys.exit("invalid sha256")
if manifest.get("sourceCommit") != source: sys.exit("manifest commit differs from the image's")
path = os.path.join(root, name)
if not isinstance(size, int) or os.path.getsize(path) != size: sys.exit("size differs from the manifest")
h = hashlib.sha256()
with open(path, "rb") as f:
    for chunk in iter(lambda: f.read(1 << 20), b""): h.update(chunk)
if h.hexdigest() != digest: sys.exit("zip does not hash to the manifest")
print(f"{name}\t{digest}\t{version}\t{size}")
PY
)" || fail "bundle validation failed"
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

#!/usr/bin/env bash
# Emit a CycloneDX SBOM for the extracted OpenVINO-direct native libraries.
# Records component licenses and a per-file SHA-256 so the redistributed native
# stack is auditable. `docker buildx build --sbom=true` additionally attaches an
# image-level SBOM attestation; this file documents the native components inside.
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=/dev/null
. "$here/onnxruntime-openvino.lock"

nat="${1:?native dir}"
out="${2:-$nat/sbom.cdx.json}"

files_json=""
for f in "$nat"/*.so*; do
  [ -f "$f" ] || continue
  h="$(sha256sum "$f" | awk '{print $1}')"
  files_json+=$(printf '{"name":"%s","hashes":[{"alg":"SHA-256","content":"%s"}]},' "$(basename "$f")" "$h")
done
files_json="${files_json%,}"

cat > "$out" <<JSON
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.5",
  "metadata": {
    "component": { "type": "library", "name": "nubarca-openvino-direct-native", "version": "${ORT_OPENVINO_VERSION}" }
  },
  "components": [
    { "type": "library", "name": "onnxruntime", "version": "${ORT_OPENVINO_VERSION}",
      "licenses": [ { "license": { "id": "MIT" } } ],
      "purl": "pkg:pypi/onnxruntime-openvino@${ORT_OPENVINO_VERSION}",
      "externalReferences": [ { "type": "distribution", "url": "${WHEEL_URL}" } ],
      "hashes": [ { "alg": "SHA-256", "content": "${WHEEL_SHA256}" } ] },
    { "type": "library", "name": "openvino", "version": "${OPENVINO_VERSION}",
      "licenses": [ { "license": { "id": "Apache-2.0" } } ] },
    { "type": "library", "name": "onetbb", "version": "bundled",
      "licenses": [ { "license": { "id": "Apache-2.0" } } ] }
  ],
  "properties": [ { "name": "nubarca:native-files", "value": "see files[]" } ],
  "files": [ ${files_json} ]
}
JSON
echo "[sbom] wrote $out"

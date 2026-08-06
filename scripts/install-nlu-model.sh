#!/usr/bin/env bash
# NubArca NLU command-model installer — FAIL-CLOSED (operator setup step only).
#
# Installs the pinned, LOCAL decoder model used by the optional "onnx" natural-
# language gallery interpreter into an operator-owned, read-only-mounted dir.
#
# TRUST MODEL (do not weaken):
#   * The expected revision is FIXED here before any download.
#   * Per-file SHA-256 checksums are read from a COMMITTED manifest
#     (scripts/nlu-sidecar/manifests/<model-key>.sha256). The manifest is the
#     single source of trust and is populated out-of-band from the model host's
#     published LFS hashes — NEVER generated from a local download.
#   * A missing manifest, a missing expected file, or ANY checksum mismatch is a
#     hard failure: the download is discarded and nothing is activated.
#   * The script NEVER writes or updates the manifest from downloaded bytes.
#   * Download goes to a temp dir; activation is an ATOMIC directory rename.
#   * The previous install is preserved as <model-key>.prev for rollback.
#
# Weights are NEVER committed to git and are NEVER downloaded outside this step.
#
# Usage:
#   NLU_MODEL_KEY=phi-4-mini-instruct-cpu-int4 \
#   NLU_MODELS_ROOT=/srv/nubarca/models/nlu \
#   ./scripts/install-nlu-model.sh
set -euo pipefail

# ---- Pinned catalogue (revision + repo + variant subdir per model key) -------
# Each entry: repo|revision|variant-subdir . Adapt the winning key after the
# target-host benchmark; the manifest for that key must be committed first.
declare -A REPO REVISION SUBDIR
REPO[phi-4-mini-instruct-cpu-int4]="microsoft/Phi-4-mini-instruct-onnx"
REVISION[phi-4-mini-instruct-cpu-int4]="fc04c8f93df696602fd9f300a30d1bf2e3081347"  # pinned commit
SUBDIR[phi-4-mini-instruct-cpu-int4]="cpu_and_mobile/cpu-int4-rtn-block-32-acc-level-4"

REPO[qwen3-4b-instruct-2507-cpu-int4]="Qwen/Qwen3-4B-Instruct-2507"     # requires a reproducible ORT-GenAI int4 export
REVISION[qwen3-4b-instruct-2507-cpu-int4]="__PIN_COMMIT_SHA__"
SUBDIR[qwen3-4b-instruct-2507-cpu-int4]="onnx-cpu-int4"                 # export output dir (see build docs)

MODEL_KEY="${NLU_MODEL_KEY:?set NLU_MODEL_KEY (e.g. phi-4-mini-instruct-cpu-int4)}"
MODELS_ROOT="${NLU_MODELS_ROOT:-/srv/nubarca/models/nlu}"
MANIFEST_DIR="$(cd "$(dirname "$0")/nlu-sidecar/manifests" && pwd)"
MANIFEST="${MANIFEST_DIR}/${MODEL_KEY}.sha256"

repo="${REPO[$MODEL_KEY]:-}"
revision="${REVISION[$MODEL_KEY]:-}"
subdir="${SUBDIR[$MODEL_KEY]:-}"
if [[ -z "$repo" || -z "$revision" || -z "$subdir" ]]; then
  echo "ERROR: unknown NLU_MODEL_KEY '$MODEL_KEY' (not in the pinned catalogue)." >&2
  exit 2
fi
if [[ "$revision" == "__PIN_COMMIT_SHA__" ]]; then
  echo "ERROR: revision for '$MODEL_KEY' is not pinned. Set an exact commit SHA in this script first." >&2
  exit 2
fi

# ---- Fail-closed: manifest MUST exist and be non-empty -----------------------
if [[ ! -s "$MANIFEST" ]]; then
  echo "ERROR: committed checksum manifest not found: $MANIFEST" >&2
  echo "       Refusing to install without pinned checksums. (Missing checksum = failure.)" >&2
  exit 3
fi

echo "NubArca NLU model install (fail-closed)"
echo "  model key : ${MODEL_KEY}"
echo "  repo      : ${repo}"
echo "  revision  : ${revision}"
echo "  variant   : ${subdir}"
echo "  manifest  : ${MANIFEST}  ($(wc -l < "$MANIFEST") file(s))"
echo "  dest root : ${MODELS_ROOT}"

# Modern CLI is `hf` (from huggingface_hub); fall back to legacy huggingface-cli.
if command -v hf >/dev/null 2>&1; then HF_DL=(hf download)
elif command -v huggingface-cli >/dev/null 2>&1; then HF_DL=(huggingface-cli download)
else echo "ERROR: neither 'hf' nor 'huggingface-cli' found (pip install huggingface_hub)." >&2; exit 4; fi
command -v sha256sum >/dev/null 2>&1 || { echo "ERROR: sha256sum not found." >&2; exit 4; }

mkdir -p "$MODELS_ROOT"
STAGE="$(mktemp -d "${MODELS_ROOT}/.stage.${MODEL_KEY}.XXXXXX")"
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

# Download ONLY the pinned revision + variant subdir (the sole allowed download).
echo "Downloading pinned revision to a staging dir…"
"${HF_DL[@]}" "$repo" \
  --revision "$revision" \
  --include "${subdir}/*" \
  --local-dir "$STAGE" >/dev/null

SRC="${STAGE}/${subdir}"
if [[ ! -d "$SRC" ]]; then
  echo "ERROR: expected variant folder missing after download: ${subdir}" >&2
  exit 5
fi

# ---- Verify EVERY manifest entry (missing/mismatch => hard failure) ----------
# Manifest format: "<sha256>  <relative/path>" (sha256sum -c compatible), paths
# relative to the variant dir. We verify from the manifest side so a file listed
# in the manifest but absent in the download is a failure too.
echo "Verifying checksums against the committed manifest…"
fail=0
while read -r want rel; do
  [[ -z "${want:-}" || "${want:0:1}" == "#" ]] && continue
  f="${SRC}/${rel}"
  if [[ ! -f "$f" ]]; then
    echo "  MISSING: ${rel}" >&2; fail=1; continue
  fi
  got="$(sha256sum "$f" | awk '{print $1}')"
  if [[ "$got" != "$want" ]]; then
    echo "  MISMATCH: ${rel}" >&2; fail=1
  fi
done < "$MANIFEST"

if [[ "$fail" -ne 0 ]]; then
  echo "ERROR: checksum verification FAILED — discarding download, NOT activating." >&2
  exit 6
fi
echo "  all checksums OK."

# ---- Atomic activation + rollback preservation -------------------------------
FINAL="${MODELS_ROOT}/${MODEL_KEY}"
if [[ -e "$FINAL" ]]; then
  rm -rf "${FINAL}.prev"
  mv "$FINAL" "${FINAL}.prev"     # keep previous install for rollback
fi
mv "$SRC" "$FINAL"                # atomic within the same filesystem
chmod -R a-w "$FINAL" 2>/dev/null || true
trap - EXIT; cleanup

echo "Installed ${MODEL_KEY} -> ${FINAL} (previous kept at ${FINAL}.prev if any)."
echo "Next: point the sidecar mount at ${FINAL} (read-only) and set"
echo "  Ai__NaturalGallerySearch__Interpreter=onnx"
echo "  Ai__NaturalGallerySearch__ModelServiceBaseUrl=http://nlu:8090"

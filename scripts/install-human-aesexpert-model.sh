#!/usr/bin/env bash
# NubArca HumanAesExpert model installer — FAIL-CLOSED (operator setup step).
#
# Installs the pinned, LOCAL KwaiVGI/HumanAesExpert-1B checkpoint (MIT) used by
# the optional Aesthetics Lab sidecar into an operator-owned, read-only-mounted
# directory.
#
# TRUST MODEL (do not weaken):
#   * The expected model repo + revision + custom-code revision are FIXED here
#     before any download.
#   * Per-file SHA-256 checksums are read from a COMMITTED manifest
#     (scripts/human-aesexpert-sidecar/manifests/<model-key>.sha256). The manifest
#     is the single source of trust, populated OUT-OF-BAND from the model host's
#     published hashes — NEVER generated from a local download.
#   * A missing manifest, a missing expected file, or ANY checksum mismatch is a
#     hard failure: the download is discarded and nothing is activated.
#   * The script NEVER writes or updates the manifest from downloaded bytes.
#   * Download goes to a temp dir; activation is an ATOMIC directory rename.
#   * The previous install is preserved as <model-key>.prev for rollback.
#
# Weights are NEVER committed to git and are NEVER downloaded outside this step.
#
# Usage:
#   NLU=... irrelevant. Example:
#   NLU_UNUSED=1 \
#   HUMANAES_MODEL_KEY=human-aesexpert-1b \
#   HUMANAES_MODELS_ROOT=/srv/nubarca/models/human-aesexpert \
#   ./scripts/install-human-aesexpert-model.sh
set -euo pipefail

# ---- Pinned catalogue (repo + revision per model key) ------------------------
# revision: pin an EXACT commit SHA (from the model repo) before first install.
declare -A REPO REVISION
# Canonical repo is KlingTeam/HumanAesExpert-1B (KwaiVGI/HumanAesExpert-1B
# redirects here). Revision + custom-code revision are the same pinned commit.
REPO[human-aesexpert-1b]="KlingTeam/HumanAesExpert-1B"
REVISION[human-aesexpert-1b]="b8f7ee3f3a1217ecd331fd6d57b6959f5c0da183"

MODEL_KEY="${HUMANAES_MODEL_KEY:-human-aesexpert-1b}"
MODELS_ROOT="${HUMANAES_MODELS_ROOT:-/srv/nubarca/models/human-aesexpert}"
MANIFEST_DIR="$(cd "$(dirname "$0")/human-aesexpert-sidecar/manifests" && pwd)"
MANIFEST="${MANIFEST_DIR}/${MODEL_KEY}.sha256"

repo="${REPO[$MODEL_KEY]:-}"
revision="${REVISION[$MODEL_KEY]:-}"
if [[ -z "$repo" || -z "$revision" ]]; then
  echo "ERROR: unknown HUMANAES_MODEL_KEY '$MODEL_KEY' (not in the pinned catalogue)." >&2
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

echo "NubArca HumanAesExpert model install (fail-closed)"
echo "  model key : ${MODEL_KEY}"
echo "  repo      : ${repo}"
echo "  revision  : ${revision}"
echo "  manifest  : ${MANIFEST}  ($(grep -cvE '^\s*(#|$)' "$MANIFEST") file(s))"
echo "  dest root : ${MODELS_ROOT}"

# Modern CLI is `hf`; fall back to legacy huggingface-cli.
if command -v hf >/dev/null 2>&1; then HF_DL=(hf download)
elif command -v huggingface-cli >/dev/null 2>&1; then HF_DL=(huggingface-cli download)
else echo "ERROR: neither 'hf' nor 'huggingface-cli' found (pip install huggingface_hub)." >&2; exit 4; fi
command -v sha256sum >/dev/null 2>&1 || { echo "ERROR: sha256sum not found." >&2; exit 4; }

mkdir -p "$MODELS_ROOT"
STAGE="$(mktemp -d "${MODELS_ROOT}/.stage.${MODEL_KEY}.XXXXXX")"
cleanup() { rm -rf "$STAGE"; }
trap cleanup EXIT

echo "Downloading pinned revision to a staging dir…"
"${HF_DL[@]}" "$repo" --revision "$revision" --local-dir "$STAGE" >/dev/null

# ---- Verify EVERY manifest entry (missing/mismatch => hard failure) ----------
# Manifest format: "<sha256>  <relative/path>" (sha256sum -c compatible).
echo "Verifying checksums against the committed manifest…"
fail=0
while read -r want rel; do
  [[ -z "${want:-}" || "${want:0:1}" == "#" ]] && continue
  f="${STAGE}/${rel}"
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
mv "$STAGE" "$FINAL"              # atomic within the same filesystem
# Read-only for EVERYONE: files r--r--r--, dirs r-xr-xr-x. The sidecar container
# runs as a NON-ROOT user (uid 10002) and mounts this dir read-only, so it must
# be world-readable + traversable; `a-w` alone can leave an owner-only 0500 dir
# that the container user cannot read.
chmod -R a+rX,a-w "$FINAL" 2>/dev/null || true
trap - EXIT

echo "Installed ${MODEL_KEY} -> ${FINAL} (previous kept at ${FINAL}.prev if any)."
echo "Next: mount ${FINAL} read-only into the sidecar (HUMANAES_MODEL_DIR) and set"
echo "  HumanAesExpert__Enabled=true"
echo "  HumanAesExpert__SidecarBaseUrl=http://human-aesexpert:8091"

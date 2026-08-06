#!/usr/bin/env bash
# NubArca HumanAesExpert controlled smoke test (operator step).
#
# Verifies the sidecar end-to-end on ONE explicitly supplied local test image and
# prints SAFE structural info only (success/failure, metric keys/count, duration,
# model/profile). It NEVER runs a gallery backfill and NEVER commits/uses private
# production photos.
#
# It talks to an ALREADY-RUNNING sidecar (start it first with the compose
# fragment). Set HUMANAES_URL to the reachable sidecar base URL. From the host
# this is typically a temporary published port or `docker exec`; on the internal
# network it is http://human-aesexpert:8091.
#
# Usage:
#   HUMANAES_URL=http://127.0.0.1:8091 \
#   ./scripts/human-aesexpert-smoke.sh /path/to/your-test-image.jpg
set -euo pipefail

URL="${HUMANAES_URL:-http://127.0.0.1:8091}"
IMAGE="${1:-}"
PROFILE_KEY="${HUMANAES_PROFILE_KEY:-human-aesexpert-1b-expert-v1}"
PREPROC="${HUMANAES_PREPROC:-human-aesexpert-official-v1}"

if [[ -z "$IMAGE" || ! -f "$IMAGE" ]]; then
  echo "Usage: HUMANAES_URL=<url> $0 <path-to-test-image>" >&2
  echo "  (do NOT use a private production photo)" >&2
  exit 2
fi
command -v curl >/dev/null 2>&1 || { echo "ERROR: curl not found." >&2; exit 3; }

echo "== HumanAesExpert smoke =="
echo "  sidecar : ${URL}"
echo "  image   : ${IMAGE} ($(wc -c < "$IMAGE") bytes)"

echo "-- readiness --"
if ! curl -fsS "${URL}/ready" >/dev/null 2>&1; then
  echo "NOT READY: the sidecar reports not-ready (model not loaded). Aborting." >&2
  exit 4
fi
echo "ready: OK"

echo "-- analyze (expert_scores) --"
resp="$(curl -fsS -X POST "${URL}/analyze" \
  -F "contractVersion=1" \
  -F "profileKey=${PROFILE_KEY}" \
  -F "capabilities=expert_scores" \
  -F "language=it" \
  -F "preprocessingProfileKey=${PREPROC}" \
  -F "image=@${IMAGE};type=image/jpeg")" || {
    echo "FAILED: the analyze call errored." >&2
    exit 5
  }

# Print SAFE structural info only (never dumps arbitrary text fields). The
# response is passed via an env var so the heredoc does not consume stdin.
if command -v python3 >/dev/null 2>&1; then
  HUMANAES_RESP="$resp" python3 - <<'PY'
import json, os
d = json.loads(os.environ["HUMANAES_RESP"])
keys = [m.get("key") for m in d.get("metrics", [])]
print("success        : True")
print("contractVersion:", d.get("contractVersion"))
print("model          :", d.get("modelName"), d.get("modelRevision"))
print("runtime        :", d.get("runtimeName"), d.get("runtimeVersion"))
print("profileKey     :", d.get("profileKey"))
print("preprocessing  :", d.get("preprocessingProfileKey"))
print("capabilities   :", d.get("completedCapabilities"))
print("metric count   :", len(keys))
print("metric keys    :", keys)
print("durationMs     :", d.get("durationMs"))
print("warnings       :", d.get("warnings"))
PY
else
  echo "(python3 not found; raw metric count via grep)"
  echo "metric count   : $(grep -o '"key"' <<<"$resp" | wc -l)"
fi

echo "== done (no backfill run, no image stored) =="

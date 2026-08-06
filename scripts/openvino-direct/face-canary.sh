#!/usr/bin/env bash
# =============================================================================
# Face AI milestone — ISOLATED production canary (Part 6 + Part 8).
#
# One-shot, throwaway validation that NEVER touches the active stack:
#   * dedicated throwaway containers/network; no prod DB/storage/creds/ports;
#   * models + fixture read-only; read-only root; bounded tmpfs; cap_drop ALL;
#     no-new-privileges; mem/CPU limits; /dev/dri + detected render group only
#     where a GPU device is used; automatic cleanup on success/failure/signal;
#   * before/after assertion that live prod container IDs + uptimes are unchanged.
#
# Two phases:
#   A) WEB-host readiness/preload lifecycle (detector GPU / recognizer CPU):
#      /health live throughout; /health/ready 503→200 only after BOTH models
#      compile + synthetic-validate; captures the state timeline + stage timings,
#      RSS/threads, and clean shutdown.
#   B) DB-free CLI harness (real OnnxFaceBackend) for the COMPLETE pipeline,
#      first-vs-warm latency (session reuse / no second compile), bounded
#      concurrency, and warm throughput + resource usage.
#
# The candidate image entrypoint is `dotnet NubArca.Api.dll`; the web host is
# launched with NO args, the CLI harness with ONLY the CLI verb + args.
# =============================================================================
set -euo pipefail

MODELS=""; FIXTURE=""; DETECTOR_DEVICE="GPU"; RECOGNIZER_DEVICE="CPU"
CONCURRENCY=4; ITERATIONS=20

while [[ $# -gt 0 ]]; do
  case "$1" in
    --models) MODELS="$2"; shift 2;;
    --fixture) FIXTURE="$2"; shift 2;;
    --detector-device) DETECTOR_DEVICE="$2"; shift 2;;
    --recognizer-device) RECOGNIZER_DEVICE="$2"; shift 2;;
    --concurrency) CONCURRENCY="$2"; shift 2;;
    --iterations) ITERATIONS="$2"; shift 2;;
    *) echo "unknown arg: $1" >&2; exit 64;;
  esac
done
[[ -d "$MODELS" && -f "$FIXTURE" ]] || { echo "--models and --fixture required" >&2; exit 64; }

cd "$(git rev-parse --show-toplevel)"
GIT_SHA="$(git rev-parse HEAD)"
IMAGE_TAG="nubarca-api:facecanary-${GIT_SHA:0:12}"
RENDER_GID=""; [[ -e /dev/dri/renderD128 ]] && RENDER_GID="$(stat -c '%g' /dev/dri/renderD128)" || true
WEB="facecanary-web-$$"

prod_snapshot() { docker ps --format '{{.ID}} {{.Names}} {{.RunningFor}} {{.Status}}' | grep -v facecanary | sort; }
BEFORE="$(prod_snapshot)"
cleanup() {
  echo "== cleanup =="
  docker rm -f "$WEB" >/dev/null 2>&1 || true
  AFTER="$(prod_snapshot)"
  if [[ "$BEFORE" == "$AFTER" ]]; then echo "prodUnchanged=true"; else echo "prodUnchanged=FALSE — INVESTIGATE" >&2; diff <(printf '%s\n' "$BEFORE") <(printf '%s\n' "$AFTER") >&2 || true; fi
}
trap cleanup EXIT

echo "== build (commit ${GIT_SHA}) =="
docker build -f src/NubArca.Api/Dockerfile --target runtime-openvino \
  --build-arg "GIT_SHA=${GIT_SHA}" -t "$IMAGE_TAG" . 1>&2

# Direct-mode env shared by web host + CLI harness.
direct_env=(
  -e Ai__Enabled=true -e Ai__Onnx__ModelDir=/models/ai
  -e Ai__FaceProfileKey=face-insightface-antelopev2-v1
  -e Ai__Onnx__ExecutionProvider=openvino-direct
  -e Ai__Onnx__OpenVino__NativeDir=/opt/nubarca/ort-openvino
  -e "Ai__Onnx__OpenVino__FaceDetectorDevice=${DETECTOR_DEVICE}"
  -e "Ai__Onnx__OpenVino__FaceRecognizerDevice=${RECOGNIZER_DEVICE}"
  -e Ai__Onnx__OpenVino__GpuPrecision=FP32 -e Ai__Onnx__OpenVino__CacheDir=/tmp/ov-cache
  -e "NUBARCA_GIT_SHA=${GIT_SHA}"
  -e "ConnectionStrings__Postgres=Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x"
)
hardening=(--read-only --tmpfs /tmp:size=512m,mode=1777 --cap-drop ALL
  --security-opt no-new-privileges --memory 6g --cpus 6
  -v "${MODELS}:/models/ai:ro" -v "${FIXTURE}:/fixture/face.jpg:ro")
gpu=(); if [[ "$DETECTOR_DEVICE" == GPU* || "$RECOGNIZER_DEVICE" == GPU* ]]; then
  [[ -n "$RENDER_GID" ]] || { echo "GPU requested but /dev/dri/renderD128 missing" >&2; exit 69; }
  gpu=(--device /dev/dri:/dev/dri --group-add "$RENDER_GID")
fi

echo "== runtime identity =="
docker run --rm --network none "${hardening[@]}" "${gpu[@]}" "${direct_env[@]}" "$IMAGE_TAG" ai onnx runtime-info

# ---------------- Phase A: web-host readiness / preload lifecycle -------------
echo "== Phase A: readiness/preload (detector=${DETECTOR_DEVICE} recognizer=${RECOGNIZER_DEVICE}) =="
docker run -d --name "$WEB" --network none "${hardening[@]}" "${gpu[@]}" "${direct_env[@]}" \
  -e Jobs__WorkerEnabled=false -e BlobJanitor__Enabled=false -e FileItemSweeper__Enabled=false \
  "$IMAGE_TAG" >/dev/null
q() { docker exec "$WEB" curl -s -o /dev/null -w '%{http_code}' "http://127.0.0.1:8080$1" 2>/dev/null || echo 000; }
qbody() { docker exec "$WEB" curl -s "http://127.0.0.1:8080$1" 2>/dev/null || true; }
t0=$(date +%s%3N); last=""; live_ok=1; ready_ms=""
for i in $(seq 1 1200); do
  live=$(q /health); ready=$(q /health/ready); body=$(qbody /health/ready)
  st=$(printf '%s' "$body" | sed -n 's/.*"state":"\([^"]*\)".*/\1/p')
  now=$(date +%s%3N); el=$((now - t0))
  [[ "$live" != "200" && -n "$st" ]] && live_ok=0
  if [[ "$st" != "$last" && -n "$st" ]]; then echo "  t=${el}ms health=${live} ready=${ready} state=${st}"; last="$st"; fi
  if [[ "$ready" == "200" ]]; then ready_ms=$el; echo "  t=${el}ms READY (health/ready=200)"; break; fi
  sleep 0.25
done
echo "livenessStayedUpDuringCompile=$([[ $live_ok -eq 1 ]] && echo true || echo false)"
echo "totalReadinessMs=${ready_ms:-TIMEOUT}"
echo "-- preload log evidence --"; docker logs "$WEB" 2>&1 | grep -iE "preload|readiness|OnnxDirect|compil|READY|native resolver" | head -20
echo "-- resource (warmed, after READY) --"
docker stats --no-stream --format 'rss={{.MemUsage}} cpu={{.CPUPerc}}' "$WEB" || true
echo "processes=$(docker top "$WEB" 2>/dev/null | tail -n +2 | wc -l) threads=$(docker exec "$WEB" sh -c 'grep -h Threads /proc/1/status' 2>/dev/null | awk '{print $2}')"
echo "-- shutdown --"; s0=$(date +%s%3N); docker stop -t 20 "$WEB" >/dev/null; s1=$(date +%s%3N)
echo "shutdownMs=$((s1 - s0))"
docker rm -f "$WEB" >/dev/null 2>&1 || true
[[ "${ready_ms:-}" == "" ]] && { echo "READINESS TIMEOUT — failing"; exit 1; }

# ---------------- Phase B: complete pipeline / first-vs-warm / concurrency -----
echo "== Phase B: first-vs-warm (concurrency 1, 10 iterations) =="
docker run --rm --network none "${hardening[@]}" "${gpu[@]}" "${direct_env[@]}" "$IMAGE_TAG" \
  ai onnx face-embed --file /fixture/face.jpg --detect --concurrency 1 --iterations 10 --timeout-seconds 300 \
  | grep -vE '^vec='

echo "== Phase B: bounded concurrency (${CONCURRENCY} callers, ${ITERATIONS} iters) =="
docker run --rm --network none "${hardening[@]}" "${gpu[@]}" "${direct_env[@]}" "$IMAGE_TAG" \
  ai onnx face-embed --file /fixture/face.jpg --detect --concurrency "$CONCURRENCY" --iterations "$ITERATIONS" --timeout-seconds 600 \
  | grep -vE '^vec='

echo "== canary complete (commit ${GIT_SHA}) =="

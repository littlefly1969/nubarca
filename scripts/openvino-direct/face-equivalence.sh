#!/usr/bin/env bash
# =============================================================================
# Face AI milestone — REAL-MODEL four-path equivalence harness (Part 5).
#
# Drives the SAME fixture through the actual application path (ai onnx face-embed
# runs the real OnnxFaceBackend — NO standalone sessions) under all four providers
# and compares DECODED results, not only raw tensors:
#
#   ortcpu   : Ai__Onnx__ExecutionProvider=onnxruntime               (reference)
#   ovcpu    : openvino-direct, FaceDetector/RecognizerDevice=CPU
#   ovgpu    : openvino-direct, ...Device=GPU, GpuPrecision=FP32
#
# (The historical 4th path — the Python OpenVINO sidecar — was removed with the
# SigLIP direct milestone; its equivalence was verified while it existed.)
#
# Recognizer acceptance : dim 512, finite, L2≈1, cosine ≥ 0.9999, maxAbsDiff ≤ 1e-4.
# Detector acceptance   : same face count; score/box/landmark within tolerance;
#                         equivalent NMS; no new false positive; no missing face;
#                         deterministic on repeat.
#
# GPU FP32 divergence is NOT hidden by relaxing thresholds — the script fails.
#
# Isolation: every container is --read-only, --cap-drop ALL, no-new-privileges,
# mem/CPU limited, models + fixture read-only; the CLI harness needs no DB (an
# unused connection string only makes AddAiSubstrate register — nothing connects).
# The candidate image entrypoint is `dotnet NubArca.Api.dll`, so containers are
# given ONLY the CLI args.
# =============================================================================
set -euo pipefail

MODELS=""; FIXTURE=""; RENDER_GID=""
COS_MIN="0.9999"; MAXABS="1e-4"; SCORE_TOL="0.01"; BOX_TOL="0.01"; LM_TOL="0.01"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --models) MODELS="$2"; shift 2;;
    --fixture) FIXTURE="$2"; shift 2;;
    *) echo "unknown arg: $1" >&2; exit 64;;
  esac
done
[[ -d "$MODELS" && -f "$FIXTURE" ]] || { echo "--models and --fixture required" >&2; exit 64; }

cd "$(git rev-parse --show-toplevel)"
GIT_SHA="$(git rev-parse HEAD)"
IMAGE_TAG="nubarca-api:facecanary-${GIT_SHA:0:12}"
[[ -e /dev/dri/renderD128 ]] && RENDER_GID="$(stat -c '%g' /dev/dri/renderD128)" || true
OUT="$(mktemp -d)"
cleanup() { rm -rf "$OUT"; }
trap cleanup EXIT

docker build -f src/NubArca.Api/Dockerfile --target runtime-openvino \
  --build-arg "GIT_SHA=${GIT_SHA}" -t "$IMAGE_TAG" . 1>&2

# Shared flags. NOTE: entrypoint already runs `dotnet NubArca.Api.dll`, so the
# container command is ONLY the CLI verb + args.
CMD=(ai onnx face-embed --file /fixture/face.jpg --detect --concurrency 1 --iterations 1 --timeout-seconds 300)
common=(--rm --read-only --tmpfs /tmp:size=512m,mode=1777
  --cap-drop ALL --security-opt no-new-privileges --memory 4g --cpus 4
  -v "${MODELS}:/models/ai:ro" -v "${FIXTURE}:/fixture/face.jpg:ro"
  -e Ai__Enabled=true -e Ai__Onnx__ModelDir=/models/ai
  -e Ai__FaceProfileKey=face-insightface-antelopev2-v1
  -e "NUBARCA_GIT_SHA=${GIT_SHA}"
  # Unused connection string: registers AddAiSubstrate; face-embed never connects.
  -e "ConnectionStrings__Postgres=Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x")

save() { grep -vE '^$' > "${OUT}/$1.txt"; }   # keep vec= + face[] + summary lines

echo "== ORT CPU (reference) =="
docker run "${common[@]}" --network none \
  -e Ai__Onnx__ExecutionProvider=onnxruntime \
  "$IMAGE_TAG" "${CMD[@]}" | tee >(grep -vE '^vec=') | save ortcpu

echo "== OpenVINO CPU =="
docker run "${common[@]}" --network none \
  -e Ai__Onnx__ExecutionProvider=openvino-direct \
  -e Ai__Onnx__OpenVino__NativeDir=/opt/nubarca/ort-openvino \
  -e Ai__Onnx__OpenVino__FaceDetectorDevice=CPU -e Ai__Onnx__OpenVino__FaceRecognizerDevice=CPU \
  -e Ai__Onnx__OpenVino__CacheDir=/tmp/ov-cache \
  "$IMAGE_TAG" "${CMD[@]}" | tee >(grep -vE '^vec=') | save ovcpu

if [[ -n "$RENDER_GID" ]]; then
  echo "== OpenVINO GPU FP32 =="
  docker run "${common[@]}" --network none --device /dev/dri:/dev/dri --group-add "$RENDER_GID" \
    -e Ai__Onnx__ExecutionProvider=openvino-direct \
    -e Ai__Onnx__OpenVino__NativeDir=/opt/nubarca/ort-openvino \
    -e Ai__Onnx__OpenVino__FaceDetectorDevice=GPU -e Ai__Onnx__OpenVino__FaceRecognizerDevice=GPU \
    -e Ai__Onnx__OpenVino__GpuPrecision=FP32 -e Ai__Onnx__OpenVino__CacheDir=/tmp/ov-cache \
    "$IMAGE_TAG" "${CMD[@]}" | tee >(grep -vE '^vec=') | save ovgpu
else
  echo "== OpenVINO GPU FP32 == SKIPPED (no /dev/dri)"
fi

echo "== compare (reference = ORT CPU) =="
python3 - "$OUT" "$COS_MIN" "$MAXABS" "$SCORE_TOL" "$BOX_TOL" "$LM_TOL" <<'PY'
import sys, math, glob, os
out, cos_min, maxabs, score_tol, box_tol, lm_tol = sys.argv[1], float(sys.argv[2]), float(sys.argv[3]), float(sys.argv[4]), float(sys.argv[5]), float(sys.argv[6])
def parse(path):
    vec=None; faces=[]
    for line in open(path):
        line=line.strip()
        if line.startswith("vec="):
            vec=[float(x) for x in line[4:].split(",") if x]
        elif line.startswith("face["):
            d={}
            for tok in line.split():
                if "=" in tok: k,v=tok.split("=",1); d[k]=v
            faces.append(d)
    return vec, faces
ref_vec, ref_faces = parse(os.path.join(out,"ortcpu.txt"))
ok=True
for path in sorted(glob.glob(os.path.join(out,"*.txt"))):
    name=os.path.basename(path)[:-4]
    if name=="ortcpu": continue
    vec,faces=parse(path)
    if vec is None or ref_vec is None or len(vec)!=len(ref_vec):
        print(f"{name}: recognizer VEC MISSING/len-mismatch"); ok=False; continue
    dot=sum(a*b for a,b in zip(ref_vec,vec)); na=math.sqrt(sum(a*a for a in ref_vec)); nb=math.sqrt(sum(b*b for b in vec))
    cos=dot/(na*nb) if na and nb else 0
    maxd=max(abs(a-b) for a,b in zip(ref_vec,vec)); l2=nb
    rec_ok = cos>=cos_min and maxd<=maxabs and abs(l2-1.0)<=1e-3
    print(f"{name}: recognizer cos={cos:.6f} maxAbsDiff={maxd:.2e} l2={l2:.6f} -> {'OK' if rec_ok else 'FAIL'}")
    det_ok = len(faces)==len(ref_faces)
    for rf,cf in zip(ref_faces,faces):
        try:
            ds=abs(float(rf['score'])-float(cf['score']))
            rb=[float(x) for x in rf['box'].split(',')]; cb=[float(x) for x in cf['box'].split(',')]
            db=max(abs(a-b) for a,b in zip(rb,cb))
            dl=0.0
            if rf.get('lm','-')!='-' and cf.get('lm','-')!='-':
                rl=[float(x) for p in rf['lm'].split(';') for x in p.split(',')]
                cl=[float(x) for p in cf['lm'].split(';') for x in p.split(',')]
                dl=max(abs(a-b) for a,b in zip(rl,cl))
            det_ok = det_ok and ds<=score_tol and db<=box_tol and dl<=lm_tol
        except Exception: det_ok=False
    print(f"{name}: detector faces={len(faces)} (ref {len(ref_faces)}) -> {'OK' if det_ok else 'FAIL'}")
    ok = ok and rec_ok and det_ok
print("== equivalence PASS ==" if ok else "== equivalence FAIL ==")
sys.exit(0 if ok else 2)
PY

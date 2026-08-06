#!/usr/bin/env bash
# =============================================================================
# SigLIP direct milestone — REAL-MODEL four-path equivalence harness.
#
# Drives the SAME image fixtures + text queries through the actual application
# path (`ai onnx image-embed --dir` / `ai onnx text-embed --queries-file` run the
# real OnnxImageEmbedder / OnnxTextEmbedder — .NET preprocessing + .NET
# tokenization in EVERY mode) under all in-process providers and compares
# vectors, token ids AND text→image rankings:
#
#   ortcpu   : Ai__Onnx__ExecutionProvider=onnxruntime               (reference)
#   ovcpu    : openvino-direct, PhotoImage/PhotoTextDevice=CPU
#   ovgpu    : openvino-direct, ...Device=GPU, GpuPrecision=FP32
#
# (The historical 4th path — the Python OpenVINO sidecar — was verified
# equivalent at commit 7ae2c00 before the sidecar client was removed:
# minCos=1.000000, maxAbsDiff≤2.2e-6, identical ids and rankings.)
#
# Acceptance per vector : dim 1152, finite, L2≈1, cosine ≥ COS_MIN,
#                         maxAbsDiff ≤ MAXABS.
# Tokenizer            : identical ids per query across ALL modes (tokenization
#                        is .NET everywhere; ids equality proves the asset +
#                        policy did not drift between modes/binaries).
# Ranking              : per query, identical top-1 and identical top-5 SET of
#                        fixture images by cosine similarity vs. the reference.
#
# GPU FP32 divergence is NOT hidden by relaxing thresholds — the script fails.
# The inline python3 comparator is a DEV TOOL for this harness only: it is not
# part of any runtime image and is never required on the server at runtime.
# =============================================================================
set -euo pipefail

MODELS=""; FIXTURES=""; QUERIES=""; RENDER_GID=""
COS_MIN="0.9999"; MAXABS="1e-4"; IMAGE_TAG_OVERRIDE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --models) MODELS="$2"; shift 2;;
    --fixtures) FIXTURES="$2"; shift 2;;
    --queries) QUERIES="$2"; shift 2;;
    --image-tag) IMAGE_TAG_OVERRIDE="$2"; shift 2;;
    --cos-min) COS_MIN="$2"; shift 2;;
    --max-abs) MAXABS="$2"; shift 2;;
    *) echo "unknown arg: $1" >&2; exit 64;;
  esac
done
[[ -d "$MODELS" && -d "$FIXTURES" && -f "$QUERIES" ]] \
  || { echo "--models <dir> --fixtures <dir> --queries <file> required" >&2; exit 64; }

cd "$(git rev-parse --show-toplevel)"
GIT_SHA="$(git rev-parse HEAD)"
IMAGE_TAG="${IMAGE_TAG_OVERRIDE:-nubarca-api:siglipeq-${GIT_SHA:0:12}}"
[[ -e /dev/dri/renderD128 ]] && RENDER_GID="$(stat -c '%g' /dev/dri/renderD128)" || true
OUT="$(mktemp -d)"
OVCACHE="siglipeq-ovcache-$$"
cleanup() {
  docker volume rm "$OVCACHE" >/dev/null 2>&1 || true
  rm -rf "$OUT"
}
trap cleanup EXIT

if [[ -z "$IMAGE_TAG_OVERRIDE" ]]; then
  docker build -f src/NubArca.Api/Dockerfile --target runtime-openvino \
    --build-arg "GIT_SHA=${GIT_SHA}" -t "$IMAGE_TAG" . 1>&2
fi
docker volume create "$OVCACHE" >/dev/null

# Shared flags. NOTE: entrypoint already runs `dotnet NubArca.Api.dll`, so the
# container command is ONLY the CLI verb + args.
IMG_CMD=(ai onnx image-embed --dir /fixtures --timeout-seconds 3600)
TXT_CMD=(ai onnx text-embed --queries-file /queries.txt --timeout-seconds 3600)
common=(--rm --read-only --tmpfs /tmp:size=512m,mode=1777
  --cap-drop ALL --security-opt no-new-privileges --memory 8g --cpus 6
  -v "${MODELS}:/models/ai:ro" -v "${FIXTURES}:/fixtures:ro" -v "${QUERIES}:/queries.txt:ro"
  -v "${OVCACHE}:/var/cache/nubarca/openvino"
  -e Ai__Enabled=true -e Ai__ImageEmbeddingsEnabled=true -e Ai__Onnx__ModelDir=/models/ai
  -e Ai__TimeoutSeconds=600
  -e "NUBARCA_GIT_SHA=${GIT_SHA}"
  # Unused connection string: registers AddAiSubstrate; the harness never connects.
  -e "ConnectionStrings__Postgres=Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x")

run_mode() { # $1 = mode name, rest = provider env args
  local name="$1"; shift
  echo "== ${name}: image tower =="
  docker run "${common[@]}" "$@" "$IMAGE_TAG" "${IMG_CMD[@]}" \
    | tee >(grep -vE '\.vec=' >&2) > "${OUT}/${name}.img.txt"
  echo "== ${name}: text tower =="
  docker run "${common[@]}" "$@" "$IMAGE_TAG" "${TXT_CMD[@]}" \
    | tee >(grep -vE '\.vec=' >&2) > "${OUT}/${name}.txt.txt"
}

run_mode ortcpu --network none \
  -e Ai__Onnx__ExecutionProvider=onnxruntime

run_mode ovcpu --network none \
  -e Ai__Onnx__ExecutionProvider=openvino-direct \
  -e Ai__Onnx__OpenVino__NativeDir=/opt/nubarca/ort-openvino \
  -e Ai__Onnx__OpenVino__PhotoImageDevice=CPU -e Ai__Onnx__OpenVino__PhotoTextDevice=CPU \
  -e Ai__Onnx__OpenVino__CacheDir=/var/cache/nubarca/openvino

if [[ -n "$RENDER_GID" ]]; then
  run_mode ovgpu --network none --device /dev/dri:/dev/dri --group-add "$RENDER_GID" \
    -e Ai__Onnx__ExecutionProvider=openvino-direct \
    -e Ai__Onnx__OpenVino__NativeDir=/opt/nubarca/ort-openvino \
    -e Ai__Onnx__OpenVino__PhotoImageDevice=GPU -e Ai__Onnx__OpenVino__PhotoTextDevice=GPU \
    -e Ai__Onnx__OpenVino__GpuPrecision=FP32 \
    -e Ai__Onnx__OpenVino__CacheDir=/var/cache/nubarca/openvino
else
  echo "== ovgpu == SKIPPED (no /dev/dri)"
fi

echo "== compare (reference = ORT CPU) =="
python3 - "$OUT" "$COS_MIN" "$MAXABS" <<'PY'
import sys, math, glob, os, re
out, cos_min, maxabs = sys.argv[1], float(sys.argv[2]), float(sys.argv[3])

def parse(path):
    vecs, ids = {}, {}
    for line in open(path):
        line = line.strip()
        m = re.match(r'^(img\[[^\]]+\]|q\[\d+\])\.vec=(.*)$', line)
        if m:
            vecs[m.group(1)] = [float(x) for x in m.group(2).split(",") if x]
            continue
        m = re.match(r'^(q\[\d+\])\.ids=(.*)$', line)
        if m:
            ids[m.group(1)] = m.group(2)
    return vecs, ids

def cos(a, b):
    dot = sum(x*y for x, y in zip(a, b))
    na = math.sqrt(sum(x*x for x in a)); nb = math.sqrt(sum(y*y for y in b))
    return dot/(na*nb) if na and nb else 0.0

def load_mode(name):
    iv, _ = parse(os.path.join(out, f"{name}.img.txt"))
    tv, ti = parse(os.path.join(out, f"{name}.txt.txt"))
    return iv, tv, ti

def ranking(iv, tv):
    ranks = {}
    for q, qv in sorted(tv.items()):
        scored = sorted(((cos(qv, v), k) for k, v in iv.items()), reverse=True)
        ranks[q] = [k for _, k in scored[:5]]
    return ranks

modes = sorted({os.path.basename(p).split(".")[0] for p in glob.glob(os.path.join(out, "*.img.txt"))})
if "ortcpu" not in modes:
    print("reference ortcpu missing"); sys.exit(2)
ref_iv, ref_tv, ref_ids = load_mode("ortcpu")
if not ref_iv or not ref_tv:
    print("reference vectors missing"); sys.exit(2)
ref_rank = ranking(ref_iv, ref_tv)
ok = True

for name in modes:
    if name == "ortcpu":
        continue
    iv, tv, ids = load_mode(name)
    for label, ref_set, cand_set in (("image", ref_iv, iv), ("text", ref_tv, tv)):
        if set(cand_set) != set(ref_set):
            print(f"{name}: {label} vector KEY SET mismatch"); ok = False; continue
        worst_cos, worst_abs, l2bad = 1.0, 0.0, []
        for k, rv in ref_set.items():
            cv = cand_set[k]
            if len(cv) != len(rv):
                print(f"{name}: {label} {k} dim mismatch"); ok = False; continue
            c = cos(rv, cv); d = max(abs(a-b) for a, b in zip(rv, cv))
            worst_cos = min(worst_cos, c); worst_abs = max(worst_abs, d)
            l2 = math.sqrt(sum(x*x for x in cv))
            if abs(l2 - 1.0) > 1e-3:
                l2bad.append(k)
            if any(map(math.isnan, cv)) or any(map(math.isinf, cv)):
                print(f"{name}: {label} {k} non-finite"); ok = False
        this_ok = worst_cos >= cos_min and worst_abs <= maxabs and not l2bad
        print(f"{name}: {label} n={len(ref_set)} minCos={worst_cos:.6f} "
              f"maxAbsDiff={worst_abs:.2e} l2bad={len(l2bad)} -> {'OK' if this_ok else 'FAIL'}")
        ok = ok and this_ok
    # Tokenizer ids must be IDENTICAL across modes (tokenization is .NET everywhere).
    tok_ok = ids == ref_ids and len(ids) > 0
    print(f"{name}: tokenizer ids identical={tok_ok} -> {'OK' if tok_ok else 'FAIL'}")
    ok = ok and tok_ok
    # Ranking: same top-1 and same top-5 SET per query as the reference.
    cand_rank = ranking(iv, tv)
    rank_ok = True
    for q, ref5 in ref_rank.items():
        c5 = cand_rank.get(q, [])
        if not c5 or c5[0] != ref5[0] or set(c5) != set(ref5):
            rank_ok = False
            print(f"{name}: ranking {q} top1={c5[:1]} vs ref {ref5[:1]}; "
                  f"top5set_equal={set(c5)==set(ref5)}")
    print(f"{name}: ranking queries={len(ref_rank)} -> {'OK' if rank_ok else 'FAIL'}")
    ok = ok and rank_ok

print("== equivalence PASS ==" if ok else "== equivalence FAIL ==")
sys.exit(0 if ok else 2)
PY

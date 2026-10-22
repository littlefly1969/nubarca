#!/usr/bin/env bash
# The full benchmark plan, run INSIDE the benchmark container:
#
#   docker run --rm --network none \
#     -v "$NUBARCA_BENCH_MODELS_DIR":/models:ro -v "$NUBARCA_BENCH_WORK_DIR":/work \
#     -e BENCH_THREADS=8 -e BENCH_ENVIRONMENT="target: <cpu>" \
#     --entrypoint /bench/run-suite.sh nubarca-docbench:vlm [lanes...]
#
# Lanes: golden lexical text chunking rerank ocr ocr-vlm visual fuse report (default: all).
# One model per process, so each manifest's peak RSS belongs to that model.
# A missing runtime or missing weights is a recorded skip, never a failed suite.
set -uo pipefail

lanes=("$@")
[ ${#lanes[@]} -eq 0 ] && lanes=(golden lexical text chunking rerank ocr ocr-vlm visual fuse report)
want() { local l; for l in "${lanes[@]}"; do [ "$l" = "$1" ] && return 0; done; return 1; }
bench() { echo "== docbench $*"; python -m docbench "$@" || echo "!! step failed: $*"; }

CHUNKINGS=(nubarca-char1600 struct-tok512-ov64)

if want golden && [ ! -f "${BENCH_GOLDEN_DIR}/manifest.json" ]; then
  bench generate --out "${BENCH_GOLDEN_DIR}"
fi

if want lexical; then
  bench text --model none --chunking nubarca-char1600 struct-tok512-ov64 struct-tok256-ov32 struct-tok512-ov0
fi

if want text; then
  # Reference first (parity anchor), then the runtimes NubArca could ship.
  for spec in e5-small:st e5-small:ort e5-small:ort-int8 e5-small:ov \
              granite-97m:st granite-97m:ort granite-97m:ov granite-97m:ov-int8 \
              granite-311m:st granite-311m:ort granite-311m:ov granite-311m:ov-int8 \
              qwen3-emb-0.6b:st qwen3-emb-0.6b:ov-int8 \
              bge-m3:st; do
    model="${spec%%:*}"; runtime="${spec##*:}"
    extra=()
    case "$model" in qwen3-emb-0.6b|granite-311m) extra+=(--matryoshka) ;; esac
    [ "$runtime" = st ] && extra+=(--save-embeddings --fusion-sweep)
    [ "$runtime" != st ] && extra+=(--save-embeddings)
    bench text --model "$model" --runtime "$runtime" --chunking "${CHUNKINGS[@]}" "${extra[@]}"
  done
fi

if want chunking; then
  # Encoder held fixed (the baseline), every chunking config.
  bench text --model e5-small --runtime ort \
    --chunking nubarca-char1600 struct-tok512-ov64 struct-tok256-ov32 struct-tok512-ov0
fi

if want rerank; then
  for reranker in bge-reranker-v2-m3 qwen3-reranker-0.6b; do
    bench text --model e5-small --runtime ort --chunking struct-tok512-ov64 \
      --rerank "$reranker" --rerank-depth 30
  done
fi

if want ocr; then
  bench ocr --engine pdf-textlayer
  bench ocr --engine tesseract --subset all
  bench ocr --engine rapidocr-ppocrv5 --subset all
fi

if want ocr-vlm; then
  for engine in paddleocr-vl-1.5-gguf xiaomi-ocr-0-gguf glm-ocr; do
    bench ocr --engine "$engine" --subset core --variants clean,scan,photo
  done
  for engine in paddleocr-vl-1.5-gguf glm-ocr; do
    bench ocr --engine "$engine" --subset core --variants clean,scan --mode region
  done
fi

if want visual; then
  for model in siglip2-so400m neomme-260m neomme-260m-st-late vultron-flash-0.8b qwen3-vl-emb-2b; do
    bench visual --model "$model" --batch 2
  done
fi

if want fuse; then
  text_run=$(ls -d "${BENCH_RESULTS_DIR}"/*-text-e5-small-st 2>/dev/null | tail -1)
  for visual_run in $(ls -d "${BENCH_RESULTS_DIR}"/*-visual-* 2>/dev/null); do
    [ -n "$text_run" ] || break
    [ -f "$visual_run/rankings.jsonl" ] || continue
    mode=dense
    grep -q '"mode": "dense"' "$visual_run/rankings.jsonl" || mode=late
    bench fuse --text-run "$text_run" --visual-run "$visual_run" --visual-mode "$mode"
  done
fi

if want report; then
  bench report
fi

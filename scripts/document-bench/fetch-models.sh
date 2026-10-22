#!/usr/bin/env bash
# Download the pinned benchmark weights (models.lock.json) into an operator-given
# directory. Run this ON THE TARGET SERVER, as its own explicit step; the benchmark
# itself never downloads anything. Every entry is fetched at its exact commit SHA;
# entries whose revision is still TODO are refused.
#
#   NUBARCA_BENCH_MODELS_DIR=<dir with ~25 GB free> scripts/document-bench/fetch-models.sh
#   ... fetch-models.sh --dry-run                  # show what would be fetched
#   ... fetch-models.sh --only e5-small,granite-97m
#   ... fetch-models.sh --include-optional         # bge-m3, Qwen3-VL, Vultron, ...
#
# Runs inside the benchmark image (build it first, see
# docs/document-intelligence/benchmark.md) so the host gets no Python packages.
set -euo pipefail

models_dir="${NUBARCA_BENCH_MODELS_DIR:?set NUBARCA_BENCH_MODELS_DIR to the directory that will hold the weights}"
image="${NUBARCA_BENCH_IMAGE:-nubarca-docbench:local}"

mkdir -p "$models_dir"
exec docker run --rm \
  --user "$(id -u):$(id -g)" \
  -e HF_HUB_OFFLINE=0 -e TRANSFORMERS_OFFLINE=0 -e HF_HOME=/tmp/hf \
  -v "$models_dir":/models \
  "$image" fetch --dest /models "$@"

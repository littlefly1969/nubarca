# Throwaway benchmark image for the NubArca document-intelligence bench.
#
# Nothing in it is a NubArca runtime component and nothing in it downloads
# weights: models are mounted read-only at /models from a directory the
# operator filled with fetch-models.sh. HF_HUB_OFFLINE / TRANSFORMERS_OFFLINE
# make an accidental download fail instead of silently happening.
#
# Targets:
#   bench (default, last stage) - golden set, metrics, lexical, Tesseract, ONNX Runtime,
#                     OpenVINO, RapidOCR, PyTorch reference lanes
#   vlm             - bench + a llama.cpp `llama-server` for GGUF OCR VLMs;
#                     requires --build-arg LLAMA_CPP_REF=<tag or commit>
#
# Build context: scripts/document-bench (see docs/document-intelligence/benchmark.md).

ARG PYTHON_IMAGE=python:3.13-slim-bookworm

FROM ${PYTHON_IMAGE} AS bench-base
ENV PYTHONDONTWRITEBYTECODE=1 \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    PIP_DISABLE_PIP_VERSION_CHECK=1 \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    HF_HUB_DISABLE_TELEMETRY=1 \
    BENCH_MODELS_DIR=/models \
    BENCH_GOLDEN_DIR=/work/golden \
    BENCH_RESULTS_DIR=/work/results

# Tesseract with the same language packs NubArca's API image ships (eng, ita).
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      tesseract-ocr tesseract-ocr-eng tesseract-ocr-ita \
      libglib2.0-0 dmidecode util-linux git \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /bench
COPY requirements-core.txt requirements-runtime.txt requirements-models.txt constraints.txt ./
RUN pip install -c constraints.txt -r requirements-core.txt -r requirements-runtime.txt \
 && pip install --index-url https://download.pytorch.org/whl/cpu torch==2.14.1 torchvision==0.29.1 \
 && pip install -c constraints.txt -r requirements-models.txt

COPY . /bench
RUN useradd --create-home --uid 10001 bench && mkdir -p /work && chown bench /work
USER bench
ENTRYPOINT ["python", "-m", "docbench"]
CMD ["--help"]

# ---- llama.cpp server for GGUF VLM OCR (optional) ----------------------------
FROM debian:bookworm-slim AS llama-build
ARG LLAMA_CPP_REF
RUN test -n "${LLAMA_CPP_REF}" || (echo "set --build-arg LLAMA_CPP_REF to a reviewed llama.cpp tag/commit" && exit 1)
RUN apt-get update \
 && apt-get install -y --no-install-recommends git cmake build-essential ca-certificates libcurl4-openssl-dev \
 && rm -rf /var/lib/apt/lists/*
RUN git clone https://github.com/ggml-org/llama.cpp /src \
 && git -C /src checkout "${LLAMA_CPP_REF}"
# GGML_NATIVE=OFF + explicit AVX2/FMA/F16C keeps the binary portable across
# 12th-gen cores (P- and E-cores share AVX2/AVX-VNNI; no AVX-512).
RUN cmake -S /src -B /build -DCMAKE_BUILD_TYPE=Release -DGGML_NATIVE=OFF \
      -DGGML_AVX=ON -DGGML_AVX2=ON -DGGML_FMA=ON -DGGML_F16C=ON -DGGML_AVX_VNNI=ON \
      -DLLAMA_CURL=OFF -DBUILD_SHARED_LIBS=OFF \
 && cmake --build /build --target llama-server -j "$(nproc)"

FROM bench-base AS vlm
COPY --from=llama-build /build/bin/llama-server /usr/local/bin/llama-server
ENV BENCH_LLAMA_SERVER=/usr/local/bin/llama-server

# Default target (last stage): the bench image without llama.cpp.
FROM bench-base AS bench

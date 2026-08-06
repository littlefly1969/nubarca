# NubArca HumanAesExpert aesthetics sidecar (internal-only, CPU).
# Weights are NEVER baked in — they are mounted read-only at HUMANAES_MODEL_DIR
# by the operator (see scripts/install-human-aesexpert-model.sh +
# docker-compose.human-aesexpert.yml). The container needs NO outbound network at
# runtime.
FROM python:3.11-slim

# Non-root runtime user.
RUN useradd --create-home --uid 10002 aes

WORKDIR /app
COPY requirements.txt /app/requirements.txt
# Build-time only: install pinned deps. At RUNTIME the container runs with no
# outbound network (see the compose service's internal-only network).
# Install CPU-only torch/torchvision FIRST from the PyTorch CPU wheel index so
# the image never pulls the CUDA runtime (the sidecar is CPU-only by design),
# then the remaining pinned deps.
RUN pip install --no-cache-dir \
        --index-url https://download.pytorch.org/whl/cpu \
        torch==2.4.1 torchvision==0.19.1 \
 && pip install --no-cache-dir -r /app/requirements.txt

COPY server.py /app/server.py
COPY model_preprocess.py /app/model_preprocess.py

ENV HUMANAES_MODEL_DIR=/models/human-aesexpert \
    HUMANAES_PORT=8091 \
    HUMANAES_MAX_QUEUE=4 \
    HUMANAES_REQUEST_TIMEOUT_S=120 \
    HF_HOME=/tmp/huggingface \
    HF_HUB_CACHE=/tmp/huggingface/hub \
    HF_MODULES_CACHE=/tmp/huggingface/modules \
    TRANSFORMERS_CACHE=/tmp/huggingface/transformers \
    XDG_CACHE_HOME=/tmp/cache \
    HF_HUB_OFFLINE=1 \
    TRANSFORMERS_OFFLINE=1 \
    PYTHONUNBUFFERED=1

USER aes
EXPOSE 8091
# Probe /ready (200 only once the model is loaded/warm), not /health (liveness).
HEALTHCHECK --interval=30s --timeout=5s --start-period=300s --retries=3 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8091/ready').status==200 else 1)" || exit 1

CMD ["python", "/app/server.py"]

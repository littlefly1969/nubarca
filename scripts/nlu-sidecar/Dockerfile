# NubArca NLU command-model sidecar (internal-only, CPU).
# Weights are NEVER baked in — they are mounted read-only at NLU_MODEL_DIR by the
# operator (see scripts/install-nlu-model.sh + docker-compose.nlu.yml). The
# container needs NO outbound network at runtime.
FROM python:3.11-slim

# Non-root runtime user.
RUN useradd --create-home --uid 10001 nlu

WORKDIR /app
COPY requirements.txt /app/requirements.txt
# Build-time only: install pinned deps. At RUNTIME the container runs with no
# outbound network (see the compose service's internal-only network).
RUN pip install --no-cache-dir -r /app/requirements.txt

COPY server.py /app/server.py

ENV NLU_MODEL_DIR=/models/nlu \
    NLU_PORT=8090 \
    NLU_MAX_QUEUE=4 \
    NLU_MAX_OUTPUT_TOKENS=200 \
    NLU_REQUEST_TIMEOUT_S=12 \
    PYTHONUNBUFFERED=1

USER nlu
EXPOSE 8090
HEALTHCHECK --interval=30s --timeout=5s --start-period=120s --retries=3 \
  CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://127.0.0.1:8090/health').status==200 else 1)" || exit 1

CMD ["python", "/app/server.py"]

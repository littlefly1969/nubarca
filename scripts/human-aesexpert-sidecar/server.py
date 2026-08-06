#!/usr/bin/env python3
"""NubArca HumanAesExpert aesthetics sidecar (LOCAL, internal-only).

Runs the pinned KwaiVGI/HumanAesExpert-1B checkpoint (MIT) on CPU and exposes
exactly the endpoints the NubArca worker needs:

    GET  /health   -> 200 {"status":"ready"|"loading"}   (liveness)
    GET  /ready    -> 200 when the model is warm, else 503 (readiness)
    POST /analyze  -> versioned aesthetic-scores response (see the contract)
                      multipart body:
                        image                 (binary; the only image transfer)
                        contractVersion       (int)
                        profileKey            (str)
                        capabilities          (comma-separated; expert_scores…)
                        language              (str, e.g. "it")
                        preprocessingProfileKey (str)

Hard operational rules (match docs/model-deployment/human-aesexpert.md):
  * one warm model instance per process; INFERENCE CONCURRENCY = 1 (a single
    semaphore) so a slow 1B CPU run can never starve Postgres / other inference;
  * a SMALL bounded queue; when full the server returns 429 (busy) immediately;
  * per-request timeout -> 504;
  * NO outbound network at runtime; weights mounted read-only at HUMANAES_MODEL_DIR;
  * NEVER log the image bytes, the (neutral) filename, metrics, or any text;
  * NEVER put a filesystem path in a response.

Expert-head output mapping (VERIFIED against the checkpoint's
modeling_internvl_chat.py `expert_score()` `names` list + arXiv:2503.23907;
scores are Mean Opinion Scores in [0,1]). The 12 outputs, in tensor order, map to
these stable NubArca contract keys:

    0  Facial Brightness                    -> facial_brightness
    1  Facial Feature Clarity               -> facial_feature_clarity
    2  Facial Skin Tone                     -> facial_skin_tone
    3  Facial Structure                     -> facial_structure
    4  Facial Contour Clarity               -> facial_contour_clarity
    5  Facial Aesthetic Score               -> facial_aesthetic
    6  Outfit                               -> outfit
    7  Body Shape                           -> body_shape
    8  Looks                                -> looks
    9  Environment                          -> environment
    10 General Appearance Aesthetic Score   -> general_appearance_aesthetic
    11 Comprehensive Aesthetic Score        -> overall_aesthetic

Fake-model mode (HUMANAES_FAKE=1) returns deterministic canned scores WITHOUT
loading torch/transformers/weights — used by the pytest suite and CI. It NEVER
represents a real inference.
"""
import asyncio
import hashlib
import io
import os
import sys
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
import uvicorn

CONTRACT_VERSION = 1
MODEL_DIR = os.environ.get("HUMANAES_MODEL_DIR", "/models/human-aesexpert")
MAX_QUEUE = int(os.environ.get("HUMANAES_MAX_QUEUE", "4"))
REQUEST_TIMEOUT_S = float(os.environ.get("HUMANAES_REQUEST_TIMEOUT_S", "120"))
PORT = int(os.environ.get("HUMANAES_PORT", "8091"))
FAKE = os.environ.get("HUMANAES_FAKE", "0") == "1"
MAX_IMAGE_BYTES = int(os.environ.get("HUMANAES_MAX_IMAGE_BYTES", str(64 * 1024 * 1024)))
TORCH_THREADS = max(1, int(os.environ.get("HUMANAES_TORCH_THREADS", "4")))
TORCH_INTEROP_THREADS = max(
    1, int(os.environ.get("HUMANAES_TORCH_INTEROP_THREADS", "1")))

# Pinned identity reported in every response (operators verify a run's provenance
# without any path leaking). Keep in sync with the installer catalogue.
# Canonical repo (KwaiVGI/HumanAesExpert-1B redirects here) + the pinned commit.
MODEL_NAME = "KlingTeam/HumanAesExpert-1B"
MODEL_REVISION = os.environ.get(
    "HUMANAES_MODEL_REVISION", "b8f7ee3f3a1217ecd331fd6d57b6959f5c0da183")
RUNTIME_NAME = "transformers"
RUNTIME_VERSION = os.environ.get("HUMANAES_RUNTIME_VERSION", "4.44.2")

# The 12 Expert-head contract keys, in the model's tensor order (see module doc).
EXPERT_KEYS = [
    "facial_brightness",
    "facial_feature_clarity",
    "facial_skin_tone",
    "facial_structure",
    "facial_contour_clarity",
    "facial_aesthetic",
    "outfit",
    "body_shape",
    "looks",
    "environment",
    "general_appearance_aesthetic",
    "overall_aesthetic",
]

@asynccontextmanager
async def _lifespan(_: "FastAPI"):
    try:
        _load()
        print("human-aesexpert-sidecar: model ready" + (" (FAKE)" if FAKE else ""), flush=True)
    except Exception as exc:  # noqa: BLE001 - surface load failure via /ready
        # Do NOT crash-loop; /ready reports not-ready so the API keeps the
        # feature unavailable. Never print image/user content (there is none).
        print(f"human-aesexpert-sidecar: model load failed: {type(exc).__name__}", flush=True)
    yield


app = FastAPI(lifespan=_lifespan)

# Concurrency = 1; the bounded queue caps how many callers may wait (extras 429).
_slot = asyncio.Semaphore(1)
_waiting = 0
_waiting_lock = asyncio.Lock()

_model = None
_tokenizer = None
_ready = False


def _load():
    """Load the pinned HumanAesExpert model + tokenizer once, at startup."""
    global _model, _tokenizer, _ready
    if FAKE:
        _ready = True
        return
    import torch  # lazy so /health can report a load failure without crashing
    from transformers import AutoModel, AutoTokenizer

    # Configure pools before the first model operation. Docker's CPU quota alone
    # does not constrain affinity: without this, PyTorch may create a pool for
    # every host logical CPU and the scheduler spreads a smaller quota across
    # P-cores, E-cores and SMT siblings. The cpuset remains an operator choice
    # because logical CPU numbering is host-specific.
    torch.set_num_threads(TORCH_THREADS)
    torch.set_num_interop_threads(TORCH_INTEROP_THREADS)

    _tokenizer = AutoTokenizer.from_pretrained(
        MODEL_DIR, trust_remote_code=True, use_fast=False
    )
    _model = AutoModel.from_pretrained(
        MODEL_DIR,
        torch_dtype=torch.float32,   # CPU
        low_cpu_mem_usage=True,
        trust_remote_code=True,
    ).eval()
    _ready = True


def _fake_scores(image_bytes: bytes):
    """Deterministic pseudo-scores in [0,1] derived from the image digest. NOT a
    real inference — only for the contract/pytest/CI path."""
    digest = hashlib.sha256(image_bytes).digest()
    return [((digest[i] / 255.0) * 0.6 + 0.2) for i in range(len(EXPERT_KEYS))]


def _expert_scores(image_bytes: bytes, preprocessing_profile: str):
    """Return a list of 12 floats in [0,1], ordered per EXPERT_KEYS."""
    if FAKE:
        return _fake_scores(image_bytes)

    from PIL import Image, ImageOps
    from model_preprocess import build_pixel_values  # shipped alongside server.py

    image = Image.open(io.BytesIO(image_bytes))
    # Consistent orientation: apply EXIF orientation, then drop metadata by
    # converting to RGB (the model uses pixels only; metadata is never a signal).
    image = ImageOps.exif_transpose(image).convert("RGB")
    pixel_values = build_pixel_values(image, preprocessing_profile)  # official-v1 = 448 tiling
    _, score_map = _model.expert_score(_tokenizer, pixel_values)
    # score_map is {ModelName: score}; remap to our ordered contract keys via the
    # model's own name order (index-aligned).
    values = list(score_map.values())
    if len(values) != len(EXPERT_KEYS):
        raise ValueError("unexpected expert-head arity")
    return [float(v) for v in values]


@app.get("/health")
def health():
    return {"status": "ready" if _ready else "loading"}


@app.get("/ready")
def ready():
    if _ready:
        return {"status": "ready"}
    return JSONResponse({"status": "loading"}, status_code=503)


@app.post("/analyze")
async def analyze(request: Request):
    global _waiting
    if not _ready:
        return JSONResponse({"error": "model_unavailable"}, status_code=503)

    form = await request.form()
    try:
        contract_version = int(form.get("contractVersion", "0"))
    except (TypeError, ValueError):
        contract_version = 0
    if contract_version != CONTRACT_VERSION:
        return JSONResponse({"error": "unsupported_contract"}, status_code=400)

    profile_key = str(form.get("profileKey", ""))
    capabilities = [c for c in str(form.get("capabilities", "")).split(",") if c]
    preprocessing_profile = str(form.get("preprocessingProfileKey", "human-aesexpert-official-v1"))

    upload = form.get("image")
    if upload is None or not hasattr(upload, "read"):
        return JSONResponse({"error": "missing_image"}, status_code=400)
    image_bytes = await upload.read()
    if not image_bytes or len(image_bytes) > MAX_IMAGE_BYTES:
        return JSONResponse({"error": "bad_image"}, status_code=400)

    # Only expert_scores is implemented in this slice; other capabilities are
    # rejected here so a mis-config never silently returns partial output.
    if capabilities != ["expert_scores"]:
        return JSONResponse({"error": "unsupported_capability"}, status_code=400)

    async with _waiting_lock:
        if _waiting >= MAX_QUEUE:
            return JSONResponse({"error": "busy"}, status_code=429)
        _waiting += 1
    try:
        async with _slot:
            started = time.monotonic()
            try:
                scores = await asyncio.wait_for(
                    asyncio.to_thread(_expert_scores, image_bytes, preprocessing_profile),
                    timeout=REQUEST_TIMEOUT_S,
                )
            except asyncio.TimeoutError:
                return JSONResponse({"error": "timeout"}, status_code=504)
            except Exception:  # noqa: BLE001 - never leak model/exception detail
                return JSONResponse({"error": "inference_failed"}, status_code=500)
            duration_ms = int((time.monotonic() - started) * 1000)
    finally:
        async with _waiting_lock:
            _waiting -= 1

    metrics = [
        {
            "key": key,
            "value": _clamp01(scores[i]),
            "scaleMin": 0.0,
            "scaleMax": 1.0,
            "confidence": None,
            "version": 1,
        }
        for i, key in enumerate(EXPERT_KEYS)
    ]

    return {
        "contractVersion": CONTRACT_VERSION,
        "profileKey": profile_key,
        "modelName": MODEL_NAME,
        "modelRevision": MODEL_REVISION,
        "runtimeName": RUNTIME_NAME,
        "runtimeVersion": RUNTIME_VERSION,
        "preprocessingProfileKey": preprocessing_profile,
        "completedCapabilities": ["expert_scores"],
        "metrics": metrics,
        "texts": [],
        "warnings": [] if not FAKE else ["fake_model: scores are not a real inference"],
        "durationMs": duration_ms,
    }


def _clamp01(v: float) -> float:
    if v != v:  # NaN
        return 0.0
    return max(0.0, min(1.0, float(v)))


if __name__ == "__main__":
    if not FAKE and not os.path.isdir(MODEL_DIR):
        print(
            f"human-aesexpert-sidecar: HUMANAES_MODEL_DIR '{MODEL_DIR}' not found — install the model first.",
            file=sys.stderr,
        )
    uvicorn.run(app, host="0.0.0.0", port=PORT, log_level="warning", access_log=False)

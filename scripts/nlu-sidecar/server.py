#!/usr/bin/env python3
"""NubArca NLU command-model sidecar (LOCAL, internal-only).

Runs a pinned instruct model under ONNX Runtime GenAI on CPU and exposes exactly
two internal endpoints for the NubArca API:

    GET  /health     -> 200 {"status":"ready"} once the model is loaded/warm
    POST /interpret  -> {"json": "<the model's JSON completion>"}
                        body: {"system": str, "user": str, "maxTokens": int}

Hard operational rules (match docs/model-deployment/nlu-command-model.md):
  * one warm model instance per process; INFERENCE CONCURRENCY = 1 (a single
    semaphore), so a decoder run can never starve Postgres / image inference.
  * a SMALL bounded queue; when full the server returns 429 (busy) immediately.
  * greedy decoding (temperature 0), strict output-token cap, NO chain-of-thought.
  * per-request timeout -> 504.
  * NO outbound network at runtime; weights are mounted read-only at NLU_MODEL_DIR.
  * user command text is NEVER logged.

This file is a deployment scaffold: it is not exercised by the .NET test suite.
Validate it on the target host during operator setup (see the deployment doc).
"""
import asyncio
import os
import re
import sys
import time

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse
import uvicorn

MODEL_DIR = os.environ.get("NLU_MODEL_DIR", "/models/nlu")
MAX_QUEUE = int(os.environ.get("NLU_MAX_QUEUE", "4"))
MAX_INPUT_TOKENS = int(os.environ.get("NLU_MAX_INPUT_TOKENS", "1024"))
MAX_OUTPUT_TOKENS = int(os.environ.get("NLU_MAX_OUTPUT_TOKENS", "200"))
REQUEST_TIMEOUT_S = float(os.environ.get("NLU_REQUEST_TIMEOUT_S", "12"))
PORT = int(os.environ.get("NLU_PORT", "8090"))

app = FastAPI()

# Concurrency = 1: only one generation runs at a time. The bounded queue caps how
# many callers may wait; extras get 429 without allocating unbounded memory.
_slot = asyncio.Semaphore(1)
_waiting = 0
_waiting_lock = asyncio.Lock()

_model = None
_tokenizer = None
_ready = False


def _load():
    """Load the pinned ONNX GenAI model + tokenizer once, at startup."""
    global _model, _tokenizer, _ready
    import onnxruntime_genai as og  # imported lazily so /health can report load errors
    _model = og.Model(MODEL_DIR)
    _tokenizer = og.Tokenizer(_model)
    _ready = True


PROMPT_STYLE = os.environ.get("NLU_PROMPT_STYLE", "chatml")  # "chatml" (Qwen/…) | "phi3"


def _prompt(system: str, user: str) -> str:
    # Explicit chat template per family (no tokenizer jinja dependency).
    if PROMPT_STYLE == "phi3":
        return f"<|system|>\n{system}<|end|>\n<|user|>\n{user}<|end|>\n<|assistant|>\n"
    # ChatML (Qwen3 etc.). "/no_think" disables Qwen3 reasoning for the turn so
    # output stays a single short JSON object (no <think> blocks, bounded latency).
    return (
        f"<|im_start|>system\n{system} /no_think<|im_end|>\n"
        f"<|im_start|>user\n{user}<|im_end|>\n"
        f"<|im_start|>assistant\n"
    )


def _generate(system: str, user: str) -> str:
    import onnxruntime_genai as og
    tokens = _tokenizer.encode(_prompt(system, user))
    if len(tokens) > MAX_INPUT_TOKENS:
        tokens = tokens[:MAX_INPUT_TOKENS]

    params = og.GeneratorParams(_model)
    # Greedy / deterministic; hard output cap; no CoT.
    params.set_search_options(do_sample=False, max_length=len(tokens) + MAX_OUTPUT_TOKENS)

    generator = og.Generator(_model, params)
    # Feed the prompt tokens. Newer ORT-GenAI (>=0.6) uses append_tokens on the
    # generator; older (<=0.5) set params.input_ids + compute_logits(). Support both.
    if hasattr(generator, "append_tokens"):
        generator.append_tokens(tokens)
        legacy = False
    else:
        params.input_ids = tokens
        generator = og.Generator(_model, params)
        legacy = True

    out = []
    while not generator.is_done():
        if legacy:
            generator.compute_logits()
        generator.generate_next_token()
        new = generator.get_next_tokens()[0]
        out.append(int(new))
    text = _tokenizer.decode(out)
    return _first_json_object(text)


def _first_json_object(text: str) -> str:
    """Return the first balanced {...} object (models sometimes wrap prose)."""
    start = text.find("{")
    if start < 0:
        return "{}"
    depth = 0
    for i in range(start, len(text)):
        if text[i] == "{":
            depth += 1
        elif text[i] == "}":
            depth -= 1
            if depth == 0:
                return text[start : i + 1]
    return text[start:]


@app.on_event("startup")
def _startup():
    try:
        _load()
        print("nlu-sidecar: model loaded, ready", flush=True)
    except Exception as exc:  # noqa: BLE001 - surface load failure via /health
        # Do NOT crash-loop; /health reports not-ready so the API stays on the
        # deterministic interpreter. Never print user text (there is none here).
        print(f"nlu-sidecar: model load failed: {type(exc).__name__}", flush=True)


@app.get("/health")
def health():
    if _ready:
        return {"status": "ready"}
    return JSONResponse({"status": "loading"}, status_code=503)


@app.post("/interpret")
async def interpret(request: Request):
    global _waiting
    if not _ready:
        return JSONResponse({"error": "model_unavailable"}, status_code=503)

    body = await request.json()
    system = str(body.get("system", ""))
    user = str(body.get("user", ""))
    max_tokens = int(body.get("maxTokens", MAX_OUTPUT_TOKENS))  # advisory; capped above

    async with _waiting_lock:
        if _waiting >= MAX_QUEUE:
            return JSONResponse({"error": "busy"}, status_code=429)
        _waiting += 1
    try:
        async with _slot:
            try:
                json_str = await asyncio.wait_for(
                    asyncio.to_thread(_generate, system, user), timeout=REQUEST_TIMEOUT_S
                )
            except asyncio.TimeoutError:
                return JSONResponse({"error": "timeout"}, status_code=504)
        return {"json": json_str}
    finally:
        async with _waiting_lock:
            _waiting -= 1


if __name__ == "__main__":
    if not os.path.isdir(MODEL_DIR):
        print(f"nlu-sidecar: NLU_MODEL_DIR '{MODEL_DIR}' not found — install the model first.", file=sys.stderr)
    uvicorn.run(app, host="0.0.0.0", port=PORT, log_level="warning", access_log=False)

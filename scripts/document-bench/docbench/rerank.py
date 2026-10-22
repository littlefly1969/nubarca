"""Optional cross-encoder rerank of the hybrid top-N chunks.

Both candidates ship a sentence-transformers CrossEncoder configuration at the
pinned revision (Qwen3-Reranker's `1_LogitScore` module scores the "yes" logit
under its own prompt template), so the official scoring path is used rather
than a re-implementation of either prompt.
"""

from __future__ import annotations

import time
from pathlib import Path

from .system import percentiles

RERANKERS = {"qwen3-reranker-0.6b": 1024, "bge-reranker-v2-m3": 1024}


def rerank_runs(name: str, models_dir: Path, threads: int, queries: list[dict],
                texts: dict[str, str], runs: dict[str, list[str]], depth: int):
    info: dict = {"model": name, "depth": depth}
    if name not in RERANKERS:
        info["skipped"] = f"unknown reranker {name}"
        return None, info
    path = models_dir / name
    if not path.exists():
        info["skipped"] = f"weights not found at {path}"
        return None, info
    try:
        import torch
        from sentence_transformers import CrossEncoder
    except ImportError as exc:
        info["skipped"] = f"sentence-transformers/torch not installed: {exc}"
        return None, info
    torch.set_num_threads(threads)
    start = time.perf_counter()
    try:
        model = CrossEncoder(str(path), device="cpu", max_length=RERANKERS[name],
                             local_files_only=True)
    except Exception as exc:  # noqa: BLE001 - recorded, not raised
        info["skipped"] = f"load failed: {type(exc).__name__}: {exc}"
        return None, info
    info["load_seconds"] = time.perf_counter() - start
    out: dict[str, list[str]] = {}
    latencies = []
    for q in queries:
        ranked = runs[q["qid"]]
        head, tail = ranked[:depth], ranked[depth:]
        t0 = time.perf_counter()
        scores = model.predict([(q["text"], texts[c]) for c in head], batch_size=8,
                               show_progress_bar=False) if head else []
        latencies.append((time.perf_counter() - t0) * 1000)
        order = sorted(range(len(head)), key=lambda i: (-float(scores[i]), i))
        out[q["qid"]] = [head[i] for i in order] + tail
    info["latency_per_query"] = percentiles(latencies)
    return out, info

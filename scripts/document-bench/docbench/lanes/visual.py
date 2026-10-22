"""Visual page-retrieval lane: page images in their STORED variant (what NubArca
holds: clean for born-digital and office files, the scan or the photo
otherwise), queries as text, page-level judgements."""

from __future__ import annotations

import time
from pathlib import Path

import numpy as np
from PIL import Image

from ..corpus.generate import page_image_path
from ..encoders import Unavailable
from ..golden import Golden, page_key
from ..system import RssSampler, percentiles, write_json
from ..visual_encoders import load_visual, maxsim
from .common import (base_manifest, docs_from_pages, evaluate_levels, finish, lock, new_run_dir,
                     write_jsonl)

DEPTH = 100


def _load_image(path: Path, max_side: int) -> Image.Image:
    image = Image.open(path).convert("RGB")
    if max_side and max(image.size) > max_side:
        scale = max_side / max(image.size)
        image = image.resize((round(image.width * scale), round(image.height * scale)),
                             Image.LANCZOS)
    return image


def run(args) -> Path:
    golden = Golden.load(args.golden)
    run_dir = new_run_dir(args.results, "visual", args.model)
    manifest = base_manifest("visual", golden, args)
    manifest.update({"model_key": args.model, "max_side": args.max_side,
                     "weights": lock(args.model)})
    keys = sorted(golden.pages)
    with RssSampler() as sampler:
        try:
            model = load_visual(args.model, args.models_dir, args.threads)
        except Unavailable as exc:
            manifest["skipped"].append({"what": args.model, "reason": str(exc)})
            manifest["metrics"] = {}
            finish(run_dir, manifest)
            print(f"SKIPPED {args.model}: {exc}")
            return run_dir
        manifest["model_load_seconds"] = model.load_seconds
        manifest["model_info"] = {"heads": list(model.heads), "late_reduce": model.late_reduce,
                                  **model.info}
        index: dict[str, list] = {h: [] for h in model.heads}
        page_ms = []
        for i in range(0, len(keys), args.batch):
            batch_keys = keys[i:i + args.batch]
            images = [_load_image(page_image_path(golden.root, d, p,
                                                  golden.docs[d]["stored_variant"]), args.max_side)
                      for d, p in batch_keys]
            t0 = time.perf_counter()
            out = model.pages(images)
            elapsed = (time.perf_counter() - t0) * 1000 / len(batch_keys)
            page_ms.extend([elapsed] * len(batch_keys))
            for head in model.heads:
                index[head].extend(out[head])
            print(f"indexed {i + len(batch_keys)}/{len(keys)} pages ({elapsed:.0f} ms/page)",
                  flush=True)
        query_ms = []
        qvecs: dict[str, list] = {h: [] for h in model.heads}
        for q in golden.queries:
            t0 = time.perf_counter()
            out = model.query(q["text"])
            query_ms.append((time.perf_counter() - t0) * 1000)
            for head in model.heads:
                qvecs[head].append(out[head])

    page_ids = [page_key(d, p) for d, p in keys]
    runs: dict[str, dict[str, list[str]]] = {}
    score_ms: dict[str, list[float]] = {}
    cost: dict[str, dict] = {}
    for head in model.heads:
        runs[head] = {}
        score_ms[head] = []
        if head == "dense":
            mat = np.stack(index[head])
            cost[head] = {"vectors_per_page": 1, "dim": int(mat.shape[1]),
                          "bytes_per_page_fp32": int(mat.shape[1] * 4),
                          "bytes_per_page_fp16": int(mat.shape[1] * 2)}
        else:
            counts = [m.shape[0] for m in index[head]]
            dim = int(index[head][0].shape[1])
            cost[head] = {"vectors_per_page_mean": float(np.mean(counts)),
                          "vectors_per_page_max": int(max(counts)), "dim": dim,
                          "bytes_per_page_fp32": float(np.mean(counts) * dim * 4),
                          "bytes_per_page_fp16": float(np.mean(counts) * dim * 2),
                          "total_vectors": int(sum(counts)),
                          "projected_gib_per_100k_pages_fp16":
                              float(np.mean(counts) * dim * 2 * 100_000 / 2 ** 30)}
        for qi, q in enumerate(golden.queries):
            t0 = time.perf_counter()
            if head == "dense":
                scores = mat @ qvecs[head][qi]
            else:
                scores = np.array([maxsim(qvecs[head][qi], m, model.late_reduce)
                                   for m in index[head]])
            score_ms[head].append((time.perf_counter() - t0) * 1000)
            order = np.argsort(-scores, kind="stable")[:DEPTH]
            runs[head][q["qid"]] = [page_ids[j] for j in order]

    levels = {"page": runs,
              "doc": {m: {q: docs_from_pages(r) for q, r in run.items()} for m, run in runs.items()}}
    qrels = {"page": golden.page_qrels(), "doc": golden.doc_qrels()}
    baseline = "dense" if "dense" in runs else None
    metrics, rows = evaluate_levels(golden, levels, qrels, baseline_mode=baseline,
                                    resamples=args.resamples)
    manifest["index_cost"] = cost
    manifest["timings"] = {"page_encode": percentiles(page_ms),
                           "indexing_seconds_per_page": float(np.mean(page_ms)) / 1000,
                           "query_encode": percentiles(query_ms),
                           "scoring_all_pages": {h: percentiles(v) for h, v in score_ms.items()}}
    manifest["peak_rss_mib_sampled"] = sampler.peak_mib
    manifest["metrics"] = {lvl: {m: v["overall"] for m, v in modes.items()}
                           for lvl, modes in metrics.items()}
    write_json(run_dir / "metrics.json", {"metrics": metrics})
    write_jsonl(run_dir / "per_query.jsonl", rows)
    write_jsonl(run_dir / "rankings.jsonl",
                [{"level": "page", "mode": m, "qid": q, "ranked": r}
                 for m, run in runs.items() for q, r in run.items()])
    finish(run_dir, manifest)
    print(f"wrote {run_dir}")
    return run_dir

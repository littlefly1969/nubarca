"""Text retrieval lane: lexical (BM25F mirror), dense, hybrid RRF, optional rerank.

One process per (model, runtime) so the process peak RSS is that model's peak.
`--model none` runs the lexical modes only and needs no weights.
"""

from __future__ import annotations

import time
from pathlib import Path

import numpy as np

from .. import chunking
from ..encoders import TEXT_MODELS, Unavailable, load_encoder, truncate
from ..golden import Golden
from ..lexical import BM25F, rrf
from ..metrics import retrieval
from ..system import RssSampler, percentiles, write_json
from .common import (base_manifest, docs_from_pages, evaluate_levels, finish, lock, new_run_dir,
                     pages_from_chunks, write_jsonl)

DEPTH = 100
FUSION_K = (10, 30, 60, 100)
FUSION_DENSE_WEIGHT = (0.5, 1.0, 1.5, 2.0)


def _levels(chunk_runs: dict[str, dict[str, list[str]]], chunk_pages) -> dict:
    page_runs = {m: {q: pages_from_chunks(r, chunk_pages) for q, r in run.items()}
                 for m, run in chunk_runs.items()}
    doc_runs = {m: {q: docs_from_pages(r) for q, r in run.items()} for m, run in page_runs.items()}
    return {"chunk": chunk_runs, "page": page_runs, "doc": doc_runs}


def _fragmentation(golden: Golden, chunks, qrels_chunk) -> dict:
    """Answer spans no single chunk covers: a chunking defect no encoder can fix."""
    answerable = [q for q in golden.queries if q["answerable"]]
    lost = [q["qid"] for q in answerable if not any(g >= 2 for g in qrels_chunk[q["qid"]].values())]
    tokens = [chunking.approx_tokens(c.text) for c in chunks]
    return {"chunks": len(chunks), "mean_tokens": float(np.mean(tokens)),
            "p95_tokens": float(np.quantile(tokens, 0.95)), "max_tokens": int(max(tokens)),
            "answerable_queries_without_answer_chunk": len(lost), "queries": lost}


def run(args) -> Path:
    golden = Golden.load(args.golden)
    overridden = golden.load_ocr_override(args.ocr_override) if args.ocr_override else 0
    label = args.model if args.model != "none" else "lexical"
    run_dir = new_run_dir(args.results, "text", f"{label}-{args.runtime}")
    manifest = base_manifest("text", golden, args)
    manifest.update({"model_key": args.model, "runtime": args.runtime,
                     "chunking_configs": args.chunking, "ocr_override_pages": overridden,
                     "ocr_override_source": str(args.ocr_override or "")})
    queries = [q for q in golden.queries]
    qtext = [q["text"] for q in queries]

    encoder = None
    if args.model != "none":
        model = TEXT_MODELS[args.model]
        manifest["model"] = {"key": model.key, **lock(model.lock),
                             **({"export": lock(model.files[args.runtime][0])}
                                if args.runtime in model.files else {}),
                             "query_prefix": model.query_prefix,
                             "passage_prefix": model.passage_prefix, "pooling": model.pooling,
                             "padding": model.padding, "max_tokens": model.max_tokens,
                             "dim": model.dim, "matryoshka": list(model.matryoshka),
                             "notes": model.notes}

    sampler = RssSampler()
    sampler.__enter__()
    try:
        if args.model != "none":
            try:
                encoder = load_encoder(args.model, args.runtime, args.models_dir, args.threads)
                manifest["encoder"] = encoder.describe()
                manifest["model_load_seconds"] = encoder.load_seconds
            except Unavailable as exc:
                manifest["skipped"].append({"what": f"{args.model}/{args.runtime}", "reason": str(exc)})
                manifest["metrics"] = {}
                sampler.__exit__()
                finish(run_dir, manifest)
                print(f"SKIPPED {args.model}/{args.runtime}: {exc}")
                return run_dir

        all_metrics: dict = {}
        all_rows: list[dict] = []
        timings: dict = {}
        rankings: list[dict] = []
        abstention: dict = {}
        for config in args.chunking:
            chunks = chunking.build(golden, config)
            chunk_pages = {c.chunk_id: (c.doc_id, c.pages) for c in chunks}
            qrels = {"chunk": chunking.chunk_qrels(golden, chunks), "page": golden.page_qrels(),
                     "doc": golden.doc_qrels()}
            all_metrics.setdefault(config, {})["fragmentation"] = _fragmentation(
                golden, chunks, qrels["chunk"])

            lexical = BM25F([(c.chunk_id, c.title, c.heading, c.text) for c in chunks])
            chunk_runs: dict[str, dict[str, list[str]]] = {"bm25f": {}, "bm25f-gated": {}}
            top1 = {"bm25f": {}}
            for q in queries:
                hits = lexical.search(q["text"], DEPTH)
                chunk_runs["bm25f"][q["qid"]] = [k for k, _ in hits]
                chunk_runs["bm25f-gated"][q["qid"]] = [k for k, _ in
                                                       lexical.search(q["text"], DEPTH, gated=True)]
                top1["bm25f"][q["qid"]] = hits[0][1] if hits else 0.0

            if encoder is not None:
                ids = [c.chunk_id for c in chunks]
                start = time.perf_counter()
                passages = encoder.encode([c.text for c in chunks], "passage", args.batch)
                passage_seconds = time.perf_counter() - start
                query_ms = []
                qvecs = []
                for text in qtext:  # one at a time: the interactive shape
                    t0 = time.perf_counter()
                    qvecs.append(encoder.encode([text], "query", 1)[0])
                    query_ms.append((time.perf_counter() - t0) * 1000)
                qmat = np.stack(qvecs)
                timings[config] = {
                    "passages": len(chunks), "passage_seconds": passage_seconds,
                    "passages_per_second": len(chunks) / passage_seconds,
                    "approx_tokens_per_second": sum(chunking.approx_tokens(c.text)
                                                    for c in chunks) / passage_seconds,
                    "query_latency": percentiles(query_ms)}
                if args.save_embeddings:
                    np.savez_compressed(run_dir / f"embeddings-{config}.npz", chunk_ids=np.array(ids),
                                        passages=passages, qids=np.array([q["qid"] for q in queries]),
                                        queries=qmat)
                dims = [encoder.model.dim, *encoder.model.matryoshka] if args.matryoshka \
                    else [encoder.model.dim]
                for dim in dims:
                    mode = "dense" if dim == encoder.model.dim else f"dense-d{dim}"
                    sims = truncate(qmat, dim) @ truncate(passages, dim).T
                    chunk_runs[mode] = {}
                    top1[mode] = {}
                    for qi, q in enumerate(queries):
                        order = np.argsort(-sims[qi], kind="stable")[:DEPTH]
                        chunk_runs[mode][q["qid"]] = [ids[i] for i in order]
                        top1[mode][q["qid"]] = float(sims[qi, order[0]])
                    hybrid = "hybrid-rrf60" if dim == encoder.model.dim else f"hybrid-rrf60-d{dim}"
                    chunk_runs[hybrid] = {
                        q["qid"]: [k for k, _ in rrf([chunk_runs["bm25f"][q["qid"]],
                                                      chunk_runs[mode][q["qid"]]], k=60)]
                        for q in queries}
                chunk_runs["hybrid-rrf60-gated"] = {
                    q["qid"]: [k for k, _ in rrf([chunk_runs["bm25f-gated"][q["qid"]],
                                                  chunk_runs["dense"][q["qid"]]], k=60)]
                    for q in queries}
                if args.fusion_sweep:
                    for k in FUSION_K:
                        for w in FUSION_DENSE_WEIGHT:
                            chunk_runs[f"rrf-k{k}-w{w:g}"] = {
                                q["qid"]: [x for x, _ in rrf([chunk_runs["bm25f"][q["qid"]],
                                                              chunk_runs["dense"][q["qid"]]],
                                                             k=k, weights=[1.0, w])]
                                for q in queries}
                if args.rerank:
                    from ..rerank import rerank_runs
                    reranked, rerank_info = rerank_runs(
                        args.rerank, args.models_dir, args.threads, queries,
                        {c.chunk_id: c.text for c in chunks}, chunk_runs["hybrid-rrf60"],
                        args.rerank_depth)
                    if reranked is not None:
                        chunk_runs[f"hybrid-rrf60+{args.rerank}"] = reranked
                    timings.setdefault(config, {})["rerank"] = rerank_info

            levels = _levels(chunk_runs, chunk_pages)
            baseline = "bm25f"
            metrics, rows = evaluate_levels(golden, levels, qrels, baseline_mode=baseline,
                                            resamples=args.resamples)
            all_metrics[config].update(metrics)
            all_rows.extend({"chunking": config, **r} for r in rows)
            answerable = {q["qid"]: q["answerable"] for q in queries}
            abstention[config] = {mode: retrieval.abstention(scores, answerable)
                                  for mode, scores in top1.items()}
            for level in ("chunk", "page"):
                for mode, runmap in levels[level].items():
                    for qid, ranked in runmap.items():
                        rankings.append({"chunking": config, "level": level, "mode": mode,
                                         "qid": qid, "ranked": ranked[:DEPTH]})
    finally:
        sampler.__exit__()

    manifest["timings"] = timings
    manifest["peak_rss_mib_sampled"] = sampler.peak_mib
    manifest["metrics"] = {cfg: {lvl: {mode: m["overall"] for mode, m in modes.items()}
                                 if isinstance(modes, dict) and lvl != "fragmentation" else modes
                                 for lvl, modes in levels.items()}
                           for cfg, levels in all_metrics.items()}
    write_json(run_dir / "metrics.json", {"metrics": all_metrics, "abstention": abstention})
    write_jsonl(run_dir / "per_query.jsonl", all_rows)
    write_jsonl(run_dir / "rankings.jsonl", rankings)
    finish(run_dir, manifest)
    print(f"wrote {run_dir}")
    return run_dir

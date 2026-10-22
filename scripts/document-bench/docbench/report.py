"""Markdown summary of runs, paired comparison of two runs, embedding parity."""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np

from .metrics.retrieval import paired_test

PRIMARY = ("recall@5", "recall@10", "recall@20", "ndcg@5", "ndcg@10", "mrr")


def _load(run: Path) -> dict:
    manifest = json.loads((run / "manifest.json").read_text())
    metrics_path = run / "metrics.json"
    metrics = json.loads(metrics_path.read_text()) if metrics_path.exists() else {}
    return {"dir": run, "manifest": manifest, "metrics": metrics}


def _runs(results: Path) -> list[dict]:
    return [_load(p) for p in sorted(results.iterdir()) if (p / "manifest.json").exists()]


def _f(value, digits=3) -> str:
    if value is None or (isinstance(value, float) and np.isnan(value)):
        return "–"
    if isinstance(value, dict):
        if "mean" in value:
            return _f(value["mean"], digits)
        return "–"
    return f"{value:.{digits}f}" if isinstance(value, float) else str(value)


def _ci(value: dict | None) -> str:
    if not isinstance(value, dict) or "mean" not in value:
        return "–"
    return f"{value['mean']:.3f} [{value['ci_low']:.3f}, {value['ci_high']:.3f}]"


def _lat(lat: dict | None) -> str:
    if not lat or not lat.get("n"):
        return "–"
    return f"{lat['p50_ms']:.0f} / {lat['p95_ms']:.0f}"


def _rss(m: dict) -> str:
    sampled = m.get("peak_rss_mib_sampled")
    process = m.get("peak_rss_mib_process")
    value = max(v for v in (sampled, process, 0) if v is not None)
    return f"{value:.0f}"


def _text_section(runs: list[dict]) -> list[str]:
    out = ["## Text retrieval (page level unless stated)", "",
           "| run | chunking | mode | R@5 | R@10 | R@20 | nDCG@5 | nDCG@10 [95% CI] | MRR | doc R@10 |"
           " load s | query p50/p95 ms | passages/s | peak RSS MiB |",
           "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    types_rows = []
    chunk_rows = []
    abst_rows = []
    for r in runs:
        m = r["manifest"]
        name = f"{m.get('model_key')}/{m.get('runtime')}" if m.get("model_key") != "none" \
            else "lexical"
        all_metrics = r["metrics"].get("metrics", {})
        for chunking, levels in all_metrics.items():
            frag = levels.get("fragmentation", {})
            chunk_rows.append(f"| {name} | {chunking} | {frag.get('chunks')} | "
                              f"{_f(frag.get('mean_tokens'), 0)} | {_f(frag.get('p95_tokens'), 0)} | "
                              f"{frag.get('answerable_queries_without_answer_chunk')} |")
            timing = m.get("timings", {}).get(chunking, {})
            for mode, agg in levels.get("page", {}).items():
                if mode.startswith("rrf-k"):
                    continue
                o = agg["overall"]
                doc = levels.get("doc", {}).get(mode, {}).get("overall", {})
                out.append(
                    f"| {name} | {chunking} | {mode} | " + " | ".join(_f(o[k]) for k in PRIMARY[:4])
                    + f" | {_ci(o['ndcg@10'])} | {_f(o['mrr'])} | {_f(doc.get('recall@10'))} | "
                    f"{_f(m.get('model_load_seconds'), 1)} | "
                    f"{_lat(timing.get('query_latency'))} | "
                    f"{_f(timing.get('passages_per_second'), 1)} | {_rss(m)} |")
                if mode in ("bm25f", "hybrid-rrf60", "dense") or "+" in mode:
                    by_type = agg.get("by_type", {})
                    types_rows.append((f"{name} {chunking} {mode}",
                                       {t: v["ndcg@10"]["mean"] for t, v in by_type.items()}))
            for mode, a in r["metrics"].get("abstention", {}).get(chunking, {}).items():
                abst_rows.append(f"| {name} | {chunking} | {mode} | {_f(a.get('auc'))} | "
                                 f"{_f(a.get('threshold_keep95'))} | "
                                 f"{_f(a.get('unanswerable_passing_keep95'))} |")
    out += ["", "### Chunking diagnostics", "",
            "| run | chunking | chunks | mean tokens | p95 tokens | answerable queries with no answer chunk |",
            "|---|---|---|---|---|---|", *dict.fromkeys(chunk_rows)]
    if types_rows:
        types = sorted({t for _, d in types_rows for t in d})
        out += ["", "### Page nDCG@10 by query type", "",
                "| run | " + " | ".join(types) + " |", "|---|" + "---|" * len(types)]
        for label, d in types_rows:
            out.append(f"| {label} | " + " | ".join(_f(d.get(t), 2) for t in types) + " |")
    if abst_rows:
        out += ["", "### Unanswerable-query separation (top-1 score)", "",
                "| run | chunking | mode | AUC | threshold keeping 95% answerable | unanswerable passing |",
                "|---|---|---|---|---|---|", *abst_rows]
    return out


def _ocr_section(runs: list[dict]) -> list[str]:
    out = ["## OCR / parsing", "",
           "| engine | mode | variant | pages | CER | CER loose | WER | NED | word recall | "
           "reading-order err | omission | hallucination | TEDS | TEDS-S | formula match | "
           "pages/s | p50/p95 ms | failure % | load s | peak RSS MiB |",
           "|" + "---|" * 20]
    for r in runs:
        m = r["manifest"]
        metrics = r["metrics"]
        for variant, s in [("all", metrics.get("overall", {})),
                           *metrics.get("by_variant", {}).items()]:
            if not s:
                continue
            out.append(
                f"| {m.get('engine')} | {m.get('mode')} | {variant} | {s.get('pages')} | "
                f"{_ci(s.get('cer'))} | {_f(s.get('cer_loose'))} | {_f(s.get('wer'))} | "
                f"{_f(s.get('ned'))} | {_f(s.get('word_recall'))} | "
                f"{_f(s.get('reading_order_error'))} | {_f(s.get('omission_rate'))} | "
                f"{_f(s.get('hallucination_rate'))} | {_f(s.get('teds'))} | {_f(s.get('teds_s'))} | "
                f"{_f(s.get('formula_match'))} | {_f(s.get('pages_per_second'), 2)} | "
                f"{_lat(s.get('latency'))} | {100 * s.get('failure_rate', 0):.1f} | "
                f"{_f(m.get('model_load_seconds'), 1)} | {_rss(m)} |")
    return out


def _visual_section(runs: list[dict]) -> list[str]:
    out = ["## Visual page retrieval", "",
           "| model | head | R@5 | R@10 | R@20 | nDCG@5 | nDCG@10 [95% CI] | MRR | visual-only nDCG@10 |"
           " bytes/page fp32 | index s/page | query p50/p95 ms | peak RSS MiB |",
           "|" + "---|" * 13]
    for r in runs:
        m = r["manifest"]
        page = r["metrics"].get("metrics", {}).get("page", {})
        for head, agg in page.items():
            o = agg["overall"]
            vis = agg.get("by_type", {}).get("visual-only", {}).get("ndcg@10")
            cost = m.get("index_cost", {}).get(head, {})
            t = m.get("timings", {})
            out.append(f"| {m.get('model_key')} | {head} | " + " | ".join(_f(o[k]) for k in PRIMARY[:4])
                       + f" | {_ci(o['ndcg@10'])} | {_f(o['mrr'])} | {_f(vis)} | "
                       f"{_f(cost.get('bytes_per_page_fp32'), 0)} | "
                       f"{_f(t.get('indexing_seconds_per_page'), 2)} | {_lat(t.get('query_encode'))} | "
                       f"{_rss(m)} |")
    return out


def _fuse_section(runs: list[dict]) -> list[str]:
    out = ["## Text + visual fusion (page level)", "",
           "| text run | visual run | mode | R@10 | nDCG@10 [95% CI] | MRR | Δ nDCG@10 vs text (p) |",
           "|---|---|---|---|---|---|---|"]
    for r in runs:
        m = r["manifest"]
        for mode, agg in r["metrics"].get("metrics", {}).get("page", {}).items():
            o = agg["overall"]
            vs = agg.get("vs_text", {}).get("ndcg@10")
            delta = f"{vs['delta']:+.3f} (p={vs['p_value']:.3f})" if vs else "–"
            out.append(f"| {Path(m['text_run']).name} | {Path(m['visual_run']).name} | {mode} | "
                       f"{_f(o['recall@10'])} | {_ci(o['ndcg@10'])} | {_f(o['mrr'])} | {delta} |")
    return out


def write_report(results: Path, out: Path | None) -> Path:
    runs = _runs(results)
    lanes = {lane: [r for r in runs if r["manifest"].get("lane") == lane]
             for lane in ("text", "ocr", "visual", "fuse")}
    envs = sorted({r["manifest"].get("environment_label", "unlabelled") for r in runs})
    hw = runs[0]["manifest"]["hardware"] if runs else {}
    lines = ["# Document benchmark summary", "",
             "> SYNTHETIC golden set. These numbers rank candidates under controlled conditions; "
             "they are NOT a validation on any real document corpus.", "",
             f"- Environment label(s): {', '.join(envs)}",
             f"- CPU: {hw.get('model_name', 'unknown')}; RAM {hw.get('ram_total_gib', '?')} GiB",
             f"- Dataset: {runs[0]['manifest']['dataset']['version'] if runs else '–'}",
             f"- Runs: {len(runs)}", ""]
    skipped = [(r["dir"].name, s) for r in runs for s in r["manifest"].get("skipped", [])]
    if lanes["text"]:
        lines += _text_section(lanes["text"]) + [""]
    if lanes["ocr"]:
        lines += _ocr_section(lanes["ocr"]) + [""]
    if lanes["visual"]:
        lines += _visual_section(lanes["visual"]) + [""]
    if lanes["fuse"]:
        lines += _fuse_section(lanes["fuse"]) + [""]
    if skipped:
        lines += ["## Skipped", "", "| run | what | reason |", "|---|---|---|"]
        lines += [f"| {name} | {s['what']} | {s['reason']} |" for name, s in skipped]
    path = out or results / "SUMMARY.md"
    path.write_text("\n".join(lines) + "\n")
    return path


def _per_query(run: Path, level: str, mode: str, chunking: str) -> dict[str, dict]:
    out = {}
    with (run / "per_query.jsonl").open() as handle:
        for line in handle:
            row = json.loads(line)
            if row.get("level") != level or row.get("mode") != mode:
                continue
            if chunking and row.get("chunking", chunking) != chunking:
                continue
            out[row["qid"]] = row
    return out


def compare(args) -> dict:
    mode = args.mode or "hybrid-rrf60"
    base = _per_query(args.baseline, args.level, mode, args.chunking)
    cand = _per_query(args.candidate, args.level, mode, args.chunking)
    qids = sorted(set(base) & set(cand))
    if not qids:
        raise SystemExit("no overlapping queries: check --level/--mode/--chunking")
    return {"baseline": str(args.baseline), "candidate": str(args.candidate), "level": args.level,
            "mode": mode, "chunking": args.chunking, "queries": len(qids),
            "tests": {m: paired_test([base[q][m] for q in qids], [cand[q][m] for q in qids])
                      for m in ("ndcg@10", "recall@10", "recall@5", "mrr")}}


def parity(args) -> dict:
    a = np.load(args.reference / f"embeddings-{args.chunking}.npz")
    b = np.load(args.candidate / f"embeddings-{args.chunking}.npz")
    out = {}
    for ids, key in (("chunk_ids", "passages"), ("qids", "queries")):
        index = {k: i for i, k in enumerate(b[ids].tolist())}
        pairs = [(i, index[k]) for i, k in enumerate(a[ids].tolist()) if k in index]
        va = a[key][[i for i, _ in pairs]]
        vb = b[key][[j for _, j in pairs]]
        cos = (va * vb).sum(axis=1) / (np.linalg.norm(va, axis=1) * np.linalg.norm(vb, axis=1))
        out[key] = {"n": len(pairs), "mean_cosine": float(cos.mean()), "min_cosine": float(cos.min()),
                    "p5_cosine": float(np.quantile(cos, 0.05))}
    qa, qb = a["queries"], b["queries"]
    pa, pb = a["passages"], b["passages"]
    top_a = np.argsort(-(qa @ pa.T), axis=1)[:, :10]
    top_b = np.argsort(-(qb @ pb.T), axis=1)[:, :10]
    out["top10_overlap"] = float(np.mean([len(set(x) & set(y)) / 10 for x, y in zip(top_a, top_b)]))
    out["top1_agreement"] = float(np.mean(top_a[:, 0] == top_b[:, 0]))
    return out

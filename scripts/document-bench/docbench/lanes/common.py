"""Shared run plumbing: run directories, manifests, evaluation at three levels."""

from __future__ import annotations

import datetime as dt
import json
import os
import subprocess
import sys
from pathlib import Path

from .. import GENERATOR_VERSION
from ..golden import Golden, page_key
from ..metrics import retrieval
from ..system import hardware, library_versions, peak_rss_mib, thread_env, write_json

HERE = Path(__file__).resolve().parents[2]
LOCK = HERE / "models.lock.json"


def lock(name: str) -> dict:
    if not name:
        return {}
    entry = json.loads(LOCK.read_text())["models"].get(name, {})
    return {k: entry.get(k) for k in ("repo", "revision", "license", "note", "todo") if entry.get(k)}


def git_commit() -> str:
    if os.environ.get("BENCH_GIT_COMMIT"):
        return os.environ["BENCH_GIT_COMMIT"]
    try:
        return subprocess.run(["git", "-C", str(HERE), "rev-parse", "HEAD"], capture_output=True,
                              text=True, timeout=5).stdout.strip() or "unknown"
    except (OSError, subprocess.SubprocessError):
        return "unknown"


def new_run_dir(results: Path, lane: str, name: str) -> Path:
    stamp = dt.datetime.now(dt.timezone.utc).strftime("%Y%m%dT%H%M%SZ")
    safe = "".join(ch if ch.isalnum() or ch in "-_." else "-" for ch in name)
    path = results / f"{stamp}-{lane}-{safe}"
    path.mkdir(parents=True, exist_ok=False)
    return path


def base_manifest(lane: str, golden: Golden, args) -> dict:
    return {
        "lane": lane,
        "started_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        "command": sys.argv,
        "harness": {"generator_version": GENERATOR_VERSION, "git_commit": git_commit()},
        "dataset": {"version": golden.version, "truth_sha256": golden.manifest["truth_sha256"],
                    "content_sha256": golden.manifest["content_sha256"], "synthetic": True,
                    "disclaimer": golden.manifest["disclaimer"]},
        "hardware": hardware(),
        "environment_label": os.environ.get("BENCH_ENVIRONMENT", "unlabelled"),
        "libraries": library_versions(),
        "threads": getattr(args, "threads", None),
        "thread_env": thread_env(),
        "batch": getattr(args, "batch", None),
        "concurrency": 1,
        "errors": [],
        "skipped": [],
    }


def finish(run_dir: Path, manifest: dict) -> None:
    manifest["finished_utc"] = dt.datetime.now(dt.timezone.utc).isoformat()
    manifest["peak_rss_mib_process"] = peak_rss_mib()
    write_json(run_dir / "manifest.json", manifest)


def pages_from_chunks(ranked_chunks: list[str], chunk_pages: dict[str, tuple[str, list[int]]]
                      ) -> list[str]:
    out = []
    for cid in ranked_chunks:
        doc, pages = chunk_pages[cid]
        out.extend(page_key(doc, p) for p in pages)
    return retrieval.dedupe(out)


def docs_from_pages(ranked_pages: list[str]) -> list[str]:
    return retrieval.dedupe(p.split("#p")[0] for p in ranked_pages)


def evaluate_levels(golden: Golden, runs: dict[str, dict[str, list[str]]],
                    qrels_by_level: dict[str, dict[str, dict[str, int]]],
                    baseline_mode: str | None = None, resamples: int = 2000) -> tuple[dict, list]:
    """runs: {level: {mode: {qid: ranked ids}}} -> (metrics, per-query rows)."""
    types = golden.query_types()
    metrics: dict = {}
    rows = []
    for level, modes in runs.items():
        metrics[level] = {}
        per_mode = {}
        for mode, run in modes.items():
            pq = retrieval.per_query(run, qrels_by_level[level])
            per_mode[mode] = pq
            metrics[level][mode] = retrieval.aggregate(pq, types, resamples=resamples)
            for qid, values in pq.items():
                rows.append({"level": level, "mode": mode, "qid": qid, **values})
        if baseline_mode and baseline_mode in per_mode:
            base = per_mode[baseline_mode]
            for mode, pq in per_mode.items():
                if mode == baseline_mode:
                    continue
                qids = sorted(set(base) & set(pq))
                metrics[level][mode]["vs_" + baseline_mode] = {
                    name: retrieval.paired_test([base[q][name] for q in qids],
                                                [pq[q][name] for q in qids])
                    for name in ("ndcg@10", "recall@10", "mrr")}
    return metrics, rows


def write_jsonl(path: Path, rows: list[dict]) -> None:
    with path.open("w", encoding="utf-8") as handle:
        for row in rows:
            handle.write(json.dumps(row, ensure_ascii=False) + "\n")

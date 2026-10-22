"""Page-level fusion of a text run with a visual run (the fusion-config decision).

Text page rankings come from a text run's chunk ranking collapsed to pages;
visual rankings are native page rankings. Fused with weighted RRF over a
small grid, plus "visual-narrowing" — NubArca's current shape, where the
visual pass only re-orders pages the text pass also found in its top 50.
"""

from __future__ import annotations

import json
from pathlib import Path

from ..golden import Golden
from ..lexical import rrf
from ..system import write_json
from .common import base_manifest, docs_from_pages, evaluate_levels, finish, new_run_dir, write_jsonl

GRID_K = (20, 60)
GRID_VISUAL_WEIGHT = (0.25, 0.5, 1.0)


def _rankings(run: Path, level: str, mode: str, chunking: str | None) -> dict[str, list[str]]:
    out = {}
    with (run / "rankings.jsonl").open() as handle:
        for line in handle:
            row = json.loads(line)
            if row["level"] != level or row["mode"] != mode:
                continue
            if chunking and row.get("chunking", chunking) != chunking:
                continue
            out[row["qid"]] = row["ranked"]
    if not out:
        raise SystemExit(f"no {level}/{mode} rankings in {run}")
    return out


def run(args) -> Path:
    golden = Golden.load(args.golden)
    text = _rankings(args.text_run, "page", args.text_mode, args.chunking)
    visual = _rankings(args.visual_run, "page", args.visual_mode, None)
    run_dir = new_run_dir(args.results, "fuse", f"{args.text_mode}+{args.visual_mode}")
    manifest = base_manifest("fuse", golden, args)
    manifest.update({"text_run": str(args.text_run), "visual_run": str(args.visual_run),
                     "text_mode": args.text_mode, "visual_mode": args.visual_mode,
                     "chunking": args.chunking})
    qids = [q["qid"] for q in golden.queries]
    modes = {"text": {q: text.get(q, []) for q in qids},
             "visual": {q: visual.get(q, []) for q in qids}}
    for k in GRID_K:
        for w in GRID_VISUAL_WEIGHT:
            modes[f"rrf-k{k}-vw{w:g}"] = {
                q: [p for p, _ in rrf([text.get(q, []), visual.get(q, [])], k=k, weights=[1.0, w])]
                for q in qids}
    modes["visual-narrowing-top50"] = {}
    for q in qids:
        allowed = set(text.get(q, [])[:50])
        reordered = [p for p in visual.get(q, []) if p in allowed]
        modes["visual-narrowing-top50"][q] = [p for p, _ in rrf([text.get(q, []), reordered], k=60)]
    levels = {"page": modes,
              "doc": {m: {q: docs_from_pages(r) for q, r in run.items()} for m, run in modes.items()}}
    metrics, rows = evaluate_levels(golden, levels, {"page": golden.page_qrels(),
                                                     "doc": golden.doc_qrels()},
                                    baseline_mode="text", resamples=args.resamples)
    manifest["metrics"] = {lvl: {m: v["overall"] for m, v in ms.items()} for lvl, ms in metrics.items()}
    write_json(run_dir / "metrics.json", {"metrics": metrics})
    write_jsonl(run_dir / "per_query.jsonl", rows)
    finish(run_dir, manifest)
    print(f"wrote {run_dir}")
    return run_dir

"""OCR / parsing lane: every page image variant through one engine, scored
against the per-page reference text, tables and formulas.

Whole-page mode sends the page. Region mode (engines that take a task prompt)
crops each authored block using the regions the renderer recorded — an ORACLE
layout, so it measures recognition with perfect layout analysis, an upper
bound on what a layout+recognition pipeline could reach. Region mode is not
offered for the photo variant, whose geometry is warped.
"""

from __future__ import annotations

import time
from collections import defaultdict
from pathlib import Path

from PIL import Image

from ..corpus.generate import page_image_path
from ..encoders import Unavailable
from ..golden import Golden
from ..metrics import ocr as ocr_metrics
from ..metrics.retrieval import bootstrap_ci
from ..ocr_engines import ENGINE_LOCK, load_engine
from ..system import RssSampler, percentiles, write_json
from .common import base_manifest, finish, lock, new_run_dir, write_jsonl

# A fixed subset that covers every phenomenon at least once, for slow engines.
CORE_PAGES = (
    ("it-lease-residential", 1), ("it-lease-residential", 2), ("it-lease-residential", 4),
    ("it-invoice-0187", 1), ("it-invoice-0193", 1), ("it-bank-statement", 1),
    ("it-bank-statement", 2), ("it-utility-bill", 1), ("it-utility-bill", 2),
    ("it-boiler-manual", 1), ("it-boiler-manual", 3), ("it-lab-report", 1), ("it-lab-report", 2),
    ("it-financial-report", 2), ("it-financial-report", 3), ("it-newsletter", 1),
    ("it-physics-notes", 1), ("it-physics-notes", 2), ("en-paper-rfid", 2), ("en-paper-rfid", 3),
    ("en-router-manual", 1), ("en-discharge-summary", 1), ("it-project-kickoff", 3),
    ("it-budget-2026", 1),
)

SUMMARY_KEYS = ("cer", "cer_loose", "wer", "ned", "word_recall", "reading_order_error",
                "omission_rate", "hallucination_rate", "teds", "teds_s", "tables_found",
                "formula_score", "formula_match")


def select_pages(golden: Golden, subset: str) -> list[tuple[str, int]]:
    if subset == "all":
        return sorted(golden.pages)
    if subset == "core":
        return [p for p in CORE_PAGES if p in golden.pages]
    wanted = set(subset.split(","))
    return sorted(k for k in golden.pages if k[0] in wanted)


def crop(image: Image.Image, bbox: list[float], pad: float = 0.012) -> Image.Image:
    w, h = image.size
    x0, y0, x1, y1 = bbox
    box = (max(0, int((x0 - pad) * w)), max(0, int((y0 - pad) * h)),
           min(w, int((x1 + pad) * w)), min(h, int((y1 + pad) * h)))
    return image.crop(box)


def summarize(rows: list[dict], resamples: int) -> dict:
    out: dict = {"pages": len(rows)}
    ok = [r for r in rows if not r.get("error")]
    out["failure_rate"] = 1 - len(ok) / max(1, len(rows))
    for key in SUMMARY_KEYS:
        values = [r["metrics"][key] for r in ok if key in r["metrics"]]
        if values:
            mean, lo, hi = bootstrap_ci(values, resamples=resamples)
            out[key] = {"mean": mean, "ci_low": lo, "ci_high": hi, "n": len(values)}
    seconds = [r["seconds"] for r in rows]
    out["latency"] = percentiles([s * 1000 for s in seconds])
    out["pages_per_second"] = len(rows) / max(1e-9, sum(seconds))
    return out


def run(args) -> Path:
    golden = Golden.load(args.golden)
    run_dir = new_run_dir(args.results, "ocr", f"{args.engine}-{args.mode}")
    manifest = base_manifest("ocr", golden, args)
    manifest.update({"engine": args.engine, "mode": args.mode, "subset": args.subset,
                     "variants": args.variants.split(","),
                     "weights": lock(ENGINE_LOCK.get(args.engine, ""))})
    pages = select_pages(golden, args.subset)
    variants = [v for v in args.variants.split(",") if v]
    if args.engine == "pdf-textlayer":
        variants = ["clean"]
    if args.mode == "region":
        dropped = [v for v in variants if v == "photo"]
        variants = [v for v in variants if v != "photo"]
        if dropped:
            manifest["skipped"].append({"what": "photo variant",
                                        "reason": "region mode needs unwarped geometry"})

    rows: list[dict] = []
    predictions: list[dict] = []
    with RssSampler() as sampler:
        try:
            engine = load_engine(args.engine, args)
        except Unavailable as exc:
            manifest["skipped"].append({"what": args.engine, "reason": str(exc)})
            manifest["metrics"] = {}
            finish(run_dir, manifest)
            print(f"SKIPPED {args.engine}: {exc}")
            return run_dir
        manifest["engine_details"] = engine.describe()
        manifest["model_load_seconds"] = engine.load_seconds
        if args.mode == "region" and not engine.supports_region:
            manifest["skipped"].append({"what": "region mode",
                                        "reason": f"{args.engine} has no task prompts"})
            engine.close()
            manifest["metrics"] = {}
            finish(run_dir, manifest)
            return run_dir
        wall_start = time.perf_counter()
        try:
            for doc_id, page in pages:
                truth = golden.pages[(doc_id, page)]
                for variant in variants:
                    path = page_image_path(golden.root, doc_id, page, variant)
                    if not path.exists():
                        continue
                    error = ""
                    t0 = time.perf_counter()
                    try:
                        if args.engine == "pdf-textlayer":
                            text = engine.text_for(doc_id, page)
                        elif args.mode == "region":
                            image = Image.open(path)
                            parts = [engine.region(crop(image, r["bbox"]), r["kind"])
                                     for r in truth.get("regions", [])]
                            text = "\n\n".join(parts)
                        else:
                            text = engine.page(Image.open(path))
                    except Exception as exc:  # noqa: BLE001 - a failed page is a measurement
                        text, error = "", f"{type(exc).__name__}: {str(exc)[:300]}"
                    seconds = time.perf_counter() - t0
                    metrics = ocr_metrics.score_page(truth, text)
                    rows.append({"doc_id": doc_id, "page": page, "variant": variant,
                                 "format": golden.docs[doc_id]["format"], "seconds": seconds,
                                 "error": error, "metrics": metrics})
                    predictions.append({"doc_id": doc_id, "page": page, "variant": variant,
                                        "text": text, "seconds": seconds, "error": error})
                    print(f"{doc_id} p{page} {variant}: cer={metrics['cer']:.3f} "
                          f"{seconds:.1f}s{' ERROR ' + error if error else ''}", flush=True)
        finally:
            engine.close()
        wall = time.perf_counter() - wall_start

    by_variant: dict[str, list[dict]] = defaultdict(list)
    for r in rows:
        by_variant[r["variant"]].append(r)
    metrics = {"overall": summarize(rows, args.resamples),
               "by_variant": {v: summarize(rs, args.resamples) for v, rs in sorted(by_variant.items())}}
    metrics["overall"]["wall_pages_per_second"] = len(rows) / max(1e-9, wall)
    manifest["metrics"] = metrics
    manifest["peak_rss_mib_sampled"] = sampler.peak_mib
    write_json(run_dir / "metrics.json", metrics)
    write_jsonl(run_dir / "per_page.jsonl", rows)
    write_jsonl(run_dir / "predictions.jsonl", predictions)
    finish(run_dir, manifest)
    print(f"wrote {run_dir}")
    return run_dir

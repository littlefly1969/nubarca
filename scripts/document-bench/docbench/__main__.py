"""Command line: python -m docbench <command> --help"""

from __future__ import annotations

import argparse
import json
import os
import sys
from pathlib import Path

DEFAULT_GOLDEN = Path(os.environ.get("BENCH_GOLDEN_DIR", "golden"))
DEFAULT_RESULTS = Path(os.environ.get("BENCH_RESULTS_DIR", "results"))
DEFAULT_MODELS = Path(os.environ.get("BENCH_MODELS_DIR", "/models"))
DEFAULT_THREADS = int(os.environ.get("BENCH_THREADS", str(os.cpu_count() or 4)))


def _common(p: argparse.ArgumentParser, models: bool = True) -> None:
    p.add_argument("--golden", type=Path, default=DEFAULT_GOLDEN)
    p.add_argument("--results", type=Path, default=DEFAULT_RESULTS)
    p.add_argument("--threads", type=int, default=DEFAULT_THREADS)
    p.add_argument("--resamples", type=int, default=2000, help="bootstrap resamples")
    if models:
        p.add_argument("--models-dir", type=Path, default=DEFAULT_MODELS)


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(prog="docbench", description=__doc__)
    sub = parser.add_subparsers(dest="command", required=True)

    p = sub.add_parser("generate", help="generate the synthetic golden set")
    p.add_argument("--out", type=Path, default=DEFAULT_GOLDEN)
    p.add_argument("--variants", default="clean,scan,photo,lowdpi")
    p.add_argument("--only", default="")

    p = sub.add_parser("hardware", help="print the hardware/runtime description")

    p = sub.add_parser("text", help="text retrieval lane (lexical / dense / hybrid / rerank)")
    _common(p)
    p.add_argument("--model", default="none", help="text model key, or none for lexical only")
    p.add_argument("--runtime", default="st", help="st | ort | ort-int8 | ov | ov-int8")
    p.add_argument("--chunking", nargs="+", default=["nubarca-char1600", "struct-tok512-ov64"])
    p.add_argument("--batch", type=int, default=16)
    p.add_argument("--matryoshka", action="store_true", help="also score truncated dims")
    p.add_argument("--fusion-sweep", action="store_true")
    p.add_argument("--rerank", default="", help="qwen3-reranker-0.6b | bge-reranker-v2-m3")
    p.add_argument("--rerank-depth", type=int, default=30)
    p.add_argument("--ocr-override", type=Path, default=None,
                   help="predictions.jsonl of an OCR run: use its text for scan/photo docs")
    p.add_argument("--save-embeddings", action="store_true",
                   help="store vectors (synthetic data only) for the parity command")

    p = sub.add_parser("ocr", help="OCR / parsing lane")
    _common(p)
    p.add_argument("--engine", required=True)
    p.add_argument("--variants", default="clean,scan,photo,lowdpi")
    p.add_argument("--subset", default="all", help="all | core | doc ids comma-separated")
    p.add_argument("--mode", default="page", help="page | region (if the engine supports it)")
    p.add_argument("--llama-server", default=os.environ.get("BENCH_LLAMA_SERVER", "llama-server"))
    p.add_argument("--timeout", type=float, default=180.0,
                   help="seconds per page; a timeout is recorded as a failed page")
    p.add_argument("--max-tokens", type=int, default=4096)
    p.add_argument("--tesseract-omp", type=int, default=1,
                   help="OMP_THREAD_LIMIT for each tesseract process (default 1)")

    p = sub.add_parser("visual", help="visual page retrieval lane")
    _common(p)
    p.add_argument("--model", required=True)
    p.add_argument("--batch", type=int, default=2)
    p.add_argument("--max-side", type=int, default=0,
                   help="downscale pages so the longest side is at most this (0 = as stored)")

    p = sub.add_parser("fuse", help="page-level fusion of a text run and a visual run")
    _common(p, models=False)
    p.add_argument("--text-run", type=Path, required=True)
    p.add_argument("--visual-run", type=Path, required=True)
    p.add_argument("--text-mode", default="hybrid-rrf60")
    p.add_argument("--chunking", default="struct-tok512-ov64")
    p.add_argument("--visual-mode", default="dense")

    p = sub.add_parser("compare", help="paired significance of two runs (same lane)")
    p.add_argument("--baseline", type=Path, required=True)
    p.add_argument("--candidate", type=Path, required=True)
    p.add_argument("--level", default="page")
    p.add_argument("--mode", default="")
    p.add_argument("--chunking", default="")

    p = sub.add_parser("parity", help="embedding parity of two text runs (--save-embeddings)")
    p.add_argument("--reference", type=Path, required=True)
    p.add_argument("--candidate", type=Path, required=True)
    p.add_argument("--chunking", default="nubarca-char1600")

    p = sub.add_parser("report", help="Markdown summary of every run under --results")
    p.add_argument("--results", type=Path, default=DEFAULT_RESULTS)
    p.add_argument("--out", type=Path, default=None)

    p = sub.add_parser("fetch", help="download pinned weights (run on the target server)")
    p.add_argument("--dest", type=Path, required=True)
    p.add_argument("--only", default="", help="comma-separated lock keys")
    p.add_argument("--include-optional", action="store_true")
    p.add_argument("--dry-run", action="store_true")
    p.add_argument("--pin-unpinned", action="store_true",
                   help="resolve pin_at_fetch entries to their current commit and record it")

    args = parser.parse_args(argv)
    if args.command == "generate":
        from .corpus.generate import main as gen
        cmd = ["--out", str(args.out), "--variants", args.variants]
        if args.only:
            cmd += ["--only", args.only]
        return gen(cmd)
    if args.command == "hardware":
        from .system import hardware, library_versions
        print(json.dumps({"hardware": hardware(), "libraries": library_versions()}, indent=2))
        return 0
    if args.command == "text":
        from .lanes import text
        text.run(args)
        return 0
    if args.command == "ocr":
        from .lanes import ocr
        ocr.run(args)
        return 0
    if args.command == "visual":
        from .lanes import visual
        visual.run(args)
        return 0
    if args.command == "fuse":
        from .lanes import fuse
        fuse.run(args)
        return 0
    if args.command == "compare":
        from .report import compare
        print(json.dumps(compare(args), indent=2))
        return 0
    if args.command == "parity":
        from .report import parity
        print(json.dumps(parity(args), indent=2))
        return 0
    if args.command == "report":
        from .report import write_report
        path = write_report(args.results, args.out)
        print(f"wrote {path}")
        return 0
    if args.command == "fetch":
        from .fetch import fetch
        return fetch(args)
    return 1


if __name__ == "__main__":
    sys.exit(main())

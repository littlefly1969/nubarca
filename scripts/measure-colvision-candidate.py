#!/usr/bin/env python3
"""Measure ONE late-interaction (ColVision-family) candidate for NubArca Phase 0.

This is an EVALUATION tool, not a runtime component. NubArca ships no production
worker for an unpromoted candidate and downloads nothing at runtime; this script
runs in a disposable model-preparation environment, is pointed at weights an
operator has already reviewed and fetched, and writes a report the .NET Phase-0
lane reads back.

What it deliberately does NOT do is decide anything. It measures cost — weights,
memory, latency, vectors per page, bytes per page — and embeds the EXACT pages
NubArca's own renderer produced, so the quality comparison happens inside
NubArca against its real dense baseline, its real MaxSim and its real evidence
gate. A benchmark that scored the model here would be scoring a notebook.

    python scripts/measure-colvision-candidate.py \
        --work   /path/to/NUBARCA_PHASE0_DIR \
        --model  vidore/colSmol-500M \
        --revision 0aaa9726104ce485884c7b8faa8a58a72d5fdbe7 \
        --license mit

Requires, in that disposable environment: torch, colpali-engine, pillow.
"""

from __future__ import annotations

import argparse
import json
import os
import resource
import shutil
import tempfile
import time
from pathlib import Path

from huggingface_hub import snapshot_download


def weight_bytes(*repos: tuple[str, str]) -> int:
    """Bytes actually on disk for the adapter and its backbone.

    Both, because an adapter's own size is a misleading number: 72 MB of LoRA is
    only runnable on top of the backbone it was trained against, and what an
    operator has to host is the sum.
    """
    total = 0
    for repo, revision in repos:
        if not repo:
            continue
        root = snapshot_download(repo, revision=revision or None)
        for base, _, files in os.walk(root):
            for name in files:
                path = os.path.join(base, name)
                # Follow the symlink into the blob store: the snapshot tree is
                # links, and `lstat` would report a few bytes per file.
                total += os.stat(path).st_size
    return total


def peak_rss_mb() -> float:
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024.0


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--work", required=True, help="NUBARCA_PHASE0_DIR")
    parser.add_argument("--model", required=True)
    parser.add_argument("--revision", required=True)
    parser.add_argument("--license", required=True)
    parser.add_argument("--base-model", default="")
    parser.add_argument("--base-revision", default="")
    parser.add_argument("--batch", type=int, default=1)
    args = parser.parse_args()

    import torch
    from PIL import Image
    from colpali_engine.models import ColIdefics3, ColIdefics3Processor

    work = Path(args.work)
    pages_dir = work / "pages"
    manifest = json.loads((work / "pages.json").read_text())
    queries = [q for q in (work / "queries.txt").read_text().splitlines() if q.strip()]

    # BOTH REVISIONS PINNED, SEPARATELY.
    #
    # The candidate is a LoRA adapter over a different repository, and PEFT
    # resolves the backbone by the name in `adapter_config.json` while
    # forwarding the ADAPTER's revision to it — a revision that does not exist
    # over there, so the load fails looking for weights at the wrong commit.
    # Materialising a local adapter copy that points at the already-pinned local
    # backbone keeps both commits explicit instead of letting one of them float.
    adapter_dir = snapshot_download(args.model, revision=args.revision)
    base_dir = snapshot_download(args.base_model, revision=args.base_revision) \
        if args.base_model else None

    local_adapter = Path(tempfile.mkdtemp(prefix="nubarca-candidate-")) / "adapter"
    shutil.copytree(adapter_dir, local_adapter, symlinks=False)
    if base_dir:
        config_path = local_adapter / "adapter_config.json"
        config = json.loads(config_path.read_text())
        config["base_model_name_or_path"] = base_dir
        config_path.write_text(json.dumps(config))

    print(
        f"loading {args.model}@{args.revision[:12]} "
        f"over {args.base_model}@{args.base_revision[:12]} on CPU",
        flush=True,
    )
    model = ColIdefics3.from_pretrained(
        str(local_adapter), dtype=torch.float32, device_map="cpu"
    ).eval()
    processor = ColIdefics3Processor.from_pretrained(str(local_adapter))

    parameters = sum(p.numel() for p in model.parameters())
    print(f"parameters {parameters:,}", flush=True)

    pages: list[dict] = []
    image_ms: list[float] = []
    dimension = 0

    for entry in manifest:
        image = Image.open(pages_dir / entry["page"]).convert("RGB")
        batch = processor.process_images([image])
        start = time.perf_counter()
        with torch.no_grad():
            out = model(**batch)
        image_ms.append((time.perf_counter() - start) * 1000.0)

        # [batch, tokens, dim] -> the one page's sequence.
        vectors = out[0].to(torch.float32).cpu().numpy()
        dimension = int(vectors.shape[-1])
        pages.append({
            "document": entry["document"],
            "vectors": [[float(x) for x in row] for row in vectors],
        })
        print(
            f"  {entry['document']}: {vectors.shape[0]} vectors x {dimension} "
            f"({image_ms[-1]:.0f} ms)",
            flush=True,
        )

    query_records: list[dict] = []
    query_ms: list[float] = []
    for query in queries:
        batch = processor.process_queries([query])
        start = time.perf_counter()
        with torch.no_grad():
            out = model(**batch)
        query_ms.append((time.perf_counter() - start) * 1000.0)
        vectors = out[0].to(torch.float32).cpu().numpy()
        query_records.append({
            "query": query,
            "vectors": [[float(x) for x in row] for row in vectors],
        })

    mean_vectors = sum(len(p["vectors"]) for p in pages) / max(1, len(pages))
    report = {
        "model": args.model,
        "revision": args.revision,
        "license": args.license,
        "parameters": parameters,
        "weightBytes": weight_bytes(
            (args.model, args.revision), (args.base_model, args.base_revision)),
        "dimension": dimension,
        "meanVectorsPerPage": mean_vectors,
        # float32 is what NubArca stores. The float16 question is deliberately
        # left open until somebody measures what halving it does to MaxSim.
        "meanFloat32BytesPerPage": mean_vectors * dimension * 4,
        # The FIRST call pays graph construction; the median is what a warm
        # worker would actually cost, and quoting the cold number as latency
        # would misdescribe both.
        "meanImageMs": sorted(image_ms)[len(image_ms) // 2] if image_ms else 0.0,
        "meanQueryMs": sorted(query_ms)[len(query_ms) // 2] if query_ms else 0.0,
        "peakRssMb": peak_rss_mb(),
        "pages": pages,
        "queries": query_records,
    }

    out_path = work / "late-vectors.json"
    out_path.write_text(json.dumps(report))
    print(
        f"wrote {out_path} pages={len(pages)} queries={len(query_records)} "
        f"dim={dimension} mean_vectors_per_page={mean_vectors:.1f} "
        f"peak_rss_mb={report['peakRssMb']:.0f}",
        flush=True,
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

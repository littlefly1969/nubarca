"""Download exactly the pinned revisions in models.lock.json. Run on the target
server only, by the operator, as an explicit step; nothing else in the harness
touches the network. Each entry lands in <dest>/<key>/ (a plain directory, not
the HF cache), with a FETCHED.json recording repo, revision and file hashes."""

from __future__ import annotations

import hashlib
import json
from pathlib import Path

from .lanes.common import LOCK


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def fetch(args) -> int:
    models = json.loads(LOCK.read_text())["models"]
    wanted = [k for k in args.only.split(",") if k] or [
        k for k, v in models.items() if args.include_optional or not v.get("optional")]
    unknown = [k for k in wanted if k not in models]
    if unknown:
        raise SystemExit(f"unknown lock keys: {unknown}")
    failures = 0
    for key in wanted:
        entry = models[key]
        revision = entry["revision"]
        if not revision or revision == "TODO" or len(revision) != 40:
            print(f"REFUSED {key}: revision not pinned ({revision}). {entry.get('todo', '')}")
            failures += 1
            continue
        target = args.dest / key
        print(f"{key}: {entry['repo']}@{revision[:12]} -> {target} ({entry['license']})")
        if args.dry_run:
            continue
        from huggingface_hub import snapshot_download

        snapshot_download(entry["repo"], revision=revision, local_dir=str(target),
                          allow_patterns=entry.get("allow"))
        files = sorted(p for p in target.rglob("*") if p.is_file() and ".cache" not in p.parts)
        record = {"repo": entry["repo"], "revision": revision, "license": entry["license"],
                  "files": {p.relative_to(target).as_posix(): _sha256(p) for p in files
                            if p.name != "FETCHED.json"}}
        (target / "FETCHED.json").write_text(json.dumps(record, indent=2) + "\n")
    return 1 if failures and not args.dry_run else 0

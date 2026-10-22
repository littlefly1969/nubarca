"""Download exactly the pinned revisions in models.lock.json. Run on the target
server only, by the operator, as an explicit step; nothing else in the harness
touches the network. Each entry lands in <dest>/<key>/ (a plain directory, not
the HF cache), with a FETCHED.json recording repo, revision and file hashes.

An entry whose revision is TODO is refused, with one deliberate exception:
`--pin-unpinned` resolves an entry marked `pin_at_fetch` to the repository's
CURRENT commit, fetches exactly that commit, and records it in
<dest>/PINNED.json. That file is the operator's to commit back into
models.lock.json; until it is, a rerun may resolve a newer commit, which the
run's FETCHED.json makes visible. A licence the card reports differently from
the lock is printed, never silently accepted."""

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


def _is_sha(revision: str | None) -> bool:
    return bool(revision) and len(revision) == 40 and all(c in "0123456789abcdef" for c in revision)


def resolve_pin(entry: dict, model_info) -> tuple[str, str | None]:
    """The commit and the card's licence for an unpinned entry, from a
    huggingface_hub `model_info` result. Fails closed on anything but a SHA."""
    sha = getattr(model_info, "sha", None)
    if not _is_sha(sha):
        raise SystemExit(f"{entry['repo']}: the hub returned no commit SHA ({sha!r}); not fetching")
    card = getattr(model_info, "card_data", None)
    licence = None
    if card is not None:
        licence = card.get("license") if hasattr(card, "get") else getattr(card, "license", None)
    return sha, licence


def fetch(args) -> int:
    models = json.loads(LOCK.read_text())["models"]
    wanted = [k for k in args.only.split(",") if k] or [
        k for k, v in models.items() if args.include_optional or not v.get("optional")]
    unknown = [k for k in wanted if k not in models]
    if unknown:
        raise SystemExit(f"unknown lock keys: {unknown}")
    failures = 0
    pinned = {}
    for key in wanted:
        entry = models[key]
        revision = entry["revision"]
        if not _is_sha(revision):
            if not (getattr(args, "pin_unpinned", False) and entry.get("pin_at_fetch")):
                hint = " (use --pin-unpinned)" if entry.get("pin_at_fetch") else ""
                print(f"REFUSED {key}: revision not pinned ({revision}){hint}. {entry.get('todo', '')}")
                failures += 1
                continue
            from huggingface_hub import HfApi

            revision, card_licence = resolve_pin(entry, HfApi().model_info(entry["repo"]))
            pinned[key] = {"repo": entry["repo"], "revision": revision,
                           "lock_license": entry["license"], "card_license": card_licence}
            print(f"PINNED {key}: {entry['repo']}@{revision} (card licence: {card_licence})")
            if card_licence and card_licence.lower() not in entry["license"].lower():
                print(f"LICENCE MISMATCH {key}: lock says {entry['license']!r}, card says "
                      f"{card_licence!r}. Measure only; resolve before promoting.")
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
        if key in pinned:
            record["pinned_at_fetch"] = True
        (target / "FETCHED.json").write_text(json.dumps(record, indent=2) + "\n")
    if pinned and not args.dry_run:
        path = args.dest / "PINNED.json"
        previous = json.loads(path.read_text()) if path.exists() else {}
        previous.update(pinned)
        path.write_text(json.dumps(previous, indent=2) + "\n")
        print(f"wrote {path}: commit these revisions into models.lock.json")
    return 1 if failures and not args.dry_run else 0

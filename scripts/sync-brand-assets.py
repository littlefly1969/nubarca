#!/usr/bin/env python3
"""Copy approved NubArca runtime assets into the platform directories.

`assets/brand/nubarca/` is the repository source of truth. Nothing here
generates, resizes, recolours or otherwise transforms artwork — the approved
package already ships every size we need, so this is a byte-exact copy and the
destination file always has the same SHA-256 as its canonical source.

    python3 scripts/sync-brand-assets.py           # copy
    python3 scripts/sync-brand-assets.py --check   # verify, write nothing

Only `runtime/` assets are ever copied. Source masters stay in the package and
reference boards are never placed anywhere a bundler could reach them.

Destination basenames are kept identical to the canonical ones so a consumer
path can be traced back to the package by name alone. The one exception is
`favicon.ico`, whose name is fixed by convention and already matches.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = ROOT / "assets" / "brand" / "nubarca"
RUNTIME = PACKAGE / "runtime"
WEB_PUBLIC = ROOT / "frontend" / "public" / "brand"
TV_ASSETS = ROOT / "tv" / "assets" / "brand"

# canonical path (relative to runtime/) -> destination directory
COPIES: list[tuple[str, Path]] = [
    # --- favicon family (fixed names required by browsers) ------------------
    *[(f"favicon/{n}", WEB_PUBLIC) for n in (
        "favicon.ico", "favicon-16.png", "favicon-24.png",
        "favicon-32.png", "favicon-48.png",
    )],
    # --- PWA / Apple ---------------------------------------------------------
    *[(f"pwa/{n}", WEB_PUBLIC) for n in (
        "nubarca-apple-touch-icon-180.png",
        "nubarca-pwa-192.png",
        "nubarca-pwa-512.png",
        "nubarca-pwa-maskable-512.png",
    )],
    # --- small UI mark, both surfaces ---------------------------------------
    # 16-256 only: the app shell never renders the mark larger, and the 512s
    # would be dead weight in the bundle.
    *[(f"web/nubarca-mark-flat-on-{surface}-{px}.png", WEB_PUBLIC)
      for surface in ("dark", "light")
      for px in (16, 24, 32, 48, 64, 128, 256)],
    # --- wordmarks -----------------------------------------------------------
    *[(f"web/{n}", WEB_PUBLIC) for n in (
        "nubarca-wordmark-on-dark-480w.png",
        "nubarca-wordmark-on-dark-960w.png",
        "nubarca-wordmark-on-dark-1440w.png",
        "nubarca-wordmark-on-light.png",
    )],
    # --- TV ------------------------------------------------------------------
    ("pwa/nubarca-expo-app-icon-1024.png", TV_ASSETS),
    *[(f"tv/{n}", TV_ASSETS) for n in (
        "nubarca-android-tv-banner-320x180.png",
        "nubarca-fire-tv-banner-1280x720.png",
        "nubarca-fire-tv-icon-512.png",
        "nubarca-tv-lockup-transparent-640w.png",
        "nubarca-tv-lockup-transparent-1280w.png",
        "nubarca-tv-lockup-transparent-1800w.png",
        "nubarca-tv-splash-1920x1080.png",
    )],
]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def manifest_index() -> dict[str, dict]:
    data = json.loads((PACKAGE / "brand-manifest.json").read_text())
    return {a["path"]: a for a in data["assets"]}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="verify only")
    args = parser.parse_args()

    index = manifest_index()
    problems: list[str] = []
    copied = 0

    for rel, dest_dir in COPIES:
        src = RUNTIME / rel
        dest = dest_dir / Path(rel).name
        canonical = f"runtime/{rel}"

        record = index.get(canonical)
        if record is None:
            problems.append(f"not catalogued in the manifest: {canonical}")
            continue
        if not record["runtimeReady"]:
            problems.append(f"refusing to ship a non-runtime-ready asset: {canonical}")
            continue
        if not src.exists():
            problems.append(f"missing from the package: {canonical}")
            continue
        if sha256(src) != record["sha256"]:
            problems.append(f"package asset does not match its manifest hash: {canonical}")
            continue

        if args.check:
            if not dest.exists():
                problems.append(f"missing consumer copy: {dest.relative_to(ROOT)}")
            elif sha256(dest) != record["sha256"]:
                problems.append(f"stale consumer copy (re-run sync): {dest.relative_to(ROOT)}")
        else:
            dest_dir.mkdir(parents=True, exist_ok=True)
            if not dest.exists() or sha256(dest) != sha256(src):
                shutil.copy2(src, dest)
                copied += 1

    # A reference board must never reach a bundled directory.
    for dest_dir in (WEB_PUBLIC, TV_ASSETS):
        if not dest_dir.exists():
            continue
        for stray in dest_dir.iterdir():
            if not stray.is_file():
                continue
            if any(k in stray.name.lower() for k in ("reference", "board", "poster", "guide")):
                problems.append(f"reference artwork inside a shipped directory: {stray.relative_to(ROOT)}")

    if problems:
        print("\n".join(f"  - {p}" for p in problems), file=sys.stderr)
        print(f"\n{len(problems)} problem(s).", file=sys.stderr)
        return 1

    if args.check:
        print(f"brand assets in sync: {len(COPIES)} consumer copies match the canonical package")
    else:
        print(f"synced {len(COPIES)} assets ({copied} written, {len(COPIES) - copied} already current)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

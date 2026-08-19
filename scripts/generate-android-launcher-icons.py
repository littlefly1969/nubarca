#!/usr/bin/env python3
"""Derive the two NubArca TV ANDROID LAUNCHER icons from the approved mark.

Why this exists
---------------
The launcher slots were being fed `nubarca-expo-app-icon-1024.png` and
`nubarca-fire-tv-icon-512.png`. Both are beautiful, and both are the wrong KIND
of artwork for a launcher: each is an opaque, full-bleed square containing a
*picture of* a rounded app icon, complete with its own frame and halo. Android
then does what it always does — draws that square as the icon, or masks it as an
adaptive foreground — and the result on a Fire TV home row is a dark rectangle
with a small logo floating in it, or a frame-inside-a-frame.

An adaptive-icon FOREGROUND in particular must be transparent and contain only
the mark: the system supplies the background (adaptiveIcon.backgroundColor) and
applies its own mask. Handing it an opaque square guarantees the square wins.

So this script produces the two things those slots actually require:

    approved flat-mark master (transparent)
      -> crop to the alpha bounding box          (the approved artwork, exactly)
      -> LEGACY  : centre it on a Midnight Navy rounded square whose OUTER
                   CORNERS ARE TRANSPARENT, with a margin all round
      -> ADAPTIVE: centre it, alone, on a fully transparent canvas, inside the
                   66/108 dp safe square Android guarantees is visible

What it deliberately does NOT do: redraw, recolour, rotate, re-letter, add a
gradient/glow/frame, change the aspect ratio of the artwork, or upscale beyond
the native artwork resolution. The pixels that survive are the approved pixels.
The Leanback/Fire TV BANNER is not touched by this script at all.

    python3 scripts/generate-android-launcher-icons.py           # write
    python3 scripts/generate-android-launcher-icons.py --check   # verify only

Run `scripts/sync-brand-assets.py` afterwards to refresh the consumer copies,
and update `assets/brand/nubarca/brand-manifest.json` + `checksums.sha256`.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = ROOT / "assets" / "brand" / "nubarca"
SOURCE = PACKAGE / "source"
RUNTIME = PACKAGE / "runtime"

# The approved transparent mark, coloured for a dark surface — which is what
# both launcher icons sit on. Read-only: this script never writes into source/.
MASTER = SOURCE / "nubarca-mark-flat-on-dark-master.png"

LEGACY_OUT = RUNTIME / "tv" / "nubarca-android-launcher-icon-512.png"
ADAPTIVE_OUT = RUNTIME / "tv" / "nubarca-android-adaptive-foreground-432.png"

LEGACY_SIDE = 512
# 108dp at xxxhdpi. This is the LARGEST adaptive layer Android ever asks for, so
# it is both the canonical size and the largest one the approved artwork can
# fill without being upscaled — see the guard in scaled().
ADAPTIVE_SIDE = 432

# Midnight Navy, the approved palette background. It is the SAME value as
# app.config.js's adaptiveIcon.backgroundColor, so the legacy icon and the
# adaptive icon read as one icon rather than two.
MIDNIGHT = (10, 15, 26, 255)

# Legacy tile geometry, as fractions of the canvas.
#   INSET  — transparent margin outside the tile, so the icon never reads as a
#            full-bleed rectangle jammed against its neighbours.
#   RADIUS — corner radius. This is what actually makes the outer corners
#            transparent, which is the defect being fixed.
LEGACY_INSET = 0.025
LEGACY_RADIUS = 0.225
# Mark width inside the legacy tile, as a fraction of the whole canvas.
LEGACY_MARK_WIDTH = 0.64

# Android adaptive icons are 108x108dp; the outer 18dp on each side is reserved
# for masking and parallax, and Android's guidance is to keep key visual
# elements within the central 66x66dp. 66/108 is where this number comes from —
# it is not a taste decision, and going above it means some launcher masks clip
# the mark.
ADAPTIVE_SAFE = 66 / 108

# Supersampling factor for the rounded tile, so its corners are cleanly
# antialiased rather than staircased.
SUPERSAMPLE = 4


def approved_artwork() -> Image.Image:
    """The master cropped to exactly the pixels that are actually drawn."""
    master = Image.open(MASTER).convert("RGBA")
    box = master.getchannel("A").getbbox()
    if box is None:
        raise SystemExit("the mark master is fully transparent")
    return master.crop(box)


def scaled(art: Image.Image, width: int) -> Image.Image:
    """Downscale the artwork to `width`, keeping its approved proportions."""
    if width > art.width:
        raise SystemExit(
            f"refusing to upscale the approved artwork ({art.width}px) to {width}px"
        )
    height = round(art.height * width / art.width)
    return art.resize((width, height), Image.Resampling.LANCZOS)


def centre(canvas: Image.Image, art: Image.Image) -> None:
    canvas.alpha_composite(
        art,
        ((canvas.width - art.width) // 2, (canvas.height - art.height) // 2),
    )


def legacy_icon(art: Image.Image) -> Image.Image:
    """Midnight tile with TRANSPARENT outer corners, mark centred on it."""
    big = LEGACY_SIDE * SUPERSAMPLE
    tile = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    inset = round(LEGACY_INSET * big)
    ImageDraw.Draw(tile).rounded_rectangle(
        (inset, inset, big - inset - 1, big - inset - 1),
        radius=round(LEGACY_RADIUS * big),
        fill=MIDNIGHT,
    )
    canvas = tile.resize((LEGACY_SIDE, LEGACY_SIDE), Image.Resampling.LANCZOS)
    centre(canvas, scaled(art, round(LEGACY_MARK_WIDTH * LEGACY_SIDE)))
    return canvas


def adaptive_foreground(art: Image.Image) -> Image.Image:
    """The mark alone on transparency, inside the adaptive safe square."""
    canvas = Image.new("RGBA", (ADAPTIVE_SIDE, ADAPTIVE_SIDE), (0, 0, 0, 0))
    safe = ADAPTIVE_SAFE * ADAPTIVE_SIDE
    # The artwork is wider than it is tall, so width is the binding dimension.
    width = round(min(safe, safe * art.width / art.height))
    centre(canvas, scaled(art, width))
    return canvas


def encode(image: Image.Image) -> bytes:
    buffer = io.BytesIO()
    # optimize=True is deterministic in zlib; no timestamp chunk is written.
    image.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def report(name: str, png: bytes) -> None:
    image = Image.open(io.BytesIO(png)).convert("RGBA")
    box = image.getchannel("A").point(lambda v: 255 if v > 8 else 0).getbbox()
    w, h = box[2] - box[0], box[3] - box[1]
    corner = image.getpixel((0, 0))
    print(f"  {name:<46} {image.width}x{image.height}  "
          f"content {w}x{h} ({w / image.width * 100:.0f}% W)  corner alpha {corner[3]}")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="verify only")
    args = parser.parse_args()

    art = approved_artwork()
    outputs = {
        LEGACY_OUT: encode(legacy_icon(art)),
        ADAPTIVE_OUT: encode(adaptive_foreground(art)),
    }

    print(f"approved artwork {art.width}x{art.height} (cropped from {MASTER.name})")
    for path, png in outputs.items():
        report(path.name, png)

    if args.check:
        stale = [p for p, data in outputs.items()
                 if not p.exists() or p.read_bytes() != data]
        if stale:
            for path in stale:
                print(f"  - not the approved derivative: {path.relative_to(ROOT)}",
                      file=sys.stderr)
            print(f"\n{len(stale)} stale file(s).", file=sys.stderr)
            return 1
        print(f"launcher icons current: {len(outputs)} files")
        return 0

    for path, data in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print(f"wrote {len(outputs)} launcher icons")
    for path in sorted(outputs):
        print(f"  {hashlib.sha256(outputs[path]).hexdigest()[:16]}  "
              f"{path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

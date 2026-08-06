#!/usr/bin/env python3
"""Derive the COMPACT NubArca flat-mark and favicon runtime sizes.

The approved flat-mark masters are drawn on a canvas far larger than the
artwork: the visible symbol occupies only 528x476 of 1024x1024 — 51.6% of the
width, 46.5% of the height, 24.0% of the area. Every derivative inherited that
padding, so a 16 px browser-tab favicon rendered roughly 10x8 px of actual
symbol and read as undersized however large the surrounding CSS box was made.
Enlarging the box could not fix it: the empty pixels scale with the artwork.

This script removes the transparent excess ONCE, at the source, and re-derives
the runtime sizes from the result:

    approved master  ->  crop to the alpha bounding box  ->  re-pad to a square
    canvas with a deliberate, uniform safe margin  ->  downscale to each size

What it deliberately does NOT do: redraw, recolour, rotate, re-letter, add a
gradient/glow/frame, change the aspect ratio of the artwork, or upscale beyond
the native artwork resolution. The pixels that survive are the approved pixels.

    python3 scripts/generate-compact-brand-marks.py           # write
    python3 scripts/generate-compact-brand-marks.py --check   # verify only

Run `scripts/sync-brand-assets.py` afterwards to refresh the consumer copies,
and update `assets/brand/nubarca/brand-manifest.json` + `checksums.sha256`.
"""

from __future__ import annotations

import argparse
import hashlib
import io
import struct
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parent.parent
PACKAGE = ROOT / "assets" / "brand" / "nubarca"
SOURCE = PACKAGE / "source"
RUNTIME = PACKAGE / "runtime"

# The approved masters. Read-only: this script never writes into source/.
MASTERS = {
    "dark": SOURCE / "nubarca-mark-flat-on-dark-master.png",
    "light": SOURCE / "nubarca-mark-flat-on-light-master.png",
}

# Flat-mark sizes shipped for the web UI.
MARK_SIZES = (16, 24, 32, 48, 64, 128, 256, 512)

# Favicon family. The favicon is the LIGHT-surface artwork (Midnight Navy on
# transparent), which is what browser tab chrome needs; the files are
# byte-identical to the matching light flat mark, as they were before.
FAVICON_SIZES = (16, 24, 32, 48, 64)
FAVICON_SURFACE = "light"
ICO_SIZES = (16, 24, 32, 48)

# Safe margin, as a fraction of the compact canvas, on the CONSTRAINING axis.
# The artwork is wider than it is tall, so width is what binds: 1/16 of the
# canvas per side puts approximately one physical pixel of transparent margin
# beside the symbol when the 16 px favicon is rasterized, and scales
# proportionally at every other size.
MARGIN_RATIO = 1 / 16


def alpha_bbox(image: Image.Image, threshold: int = 0) -> tuple[int, int, int, int]:
    """Bounds of the pixels whose alpha exceeds `threshold`."""
    mask = image.getchannel("A").point(lambda v: 255 if v > threshold else 0)
    box = mask.getbbox()
    if box is None:
        raise SystemExit("a master is fully transparent")
    return box


def compact_master(image: Image.Image, box: tuple[int, int, int, int]) -> Image.Image:
    """Crop to `box`, then centre it on a square canvas with the safe margin.

    The canvas is sized from the artwork WIDTH (the larger dimension), so the
    horizontal margin is exactly MARGIN_RATIO and the vertical margin is
    larger — the artwork keeps its approved proportions rather than being
    stretched to fill a square.
    """
    art = image.crop(box)
    canvas_side = round(art.width / (1 - 2 * MARGIN_RATIO))
    canvas = Image.new("RGBA", (canvas_side, canvas_side), (0, 0, 0, 0))
    canvas.paste(
        art,
        ((canvas_side - art.width) // 2, (canvas_side - art.height) // 2),
    )
    return canvas


def render(canvas: Image.Image, size: int) -> bytes:
    """One PNG at `size`x`size`, downscaled from the compact canvas."""
    frame = canvas.resize((size, size), Image.Resampling.LANCZOS)
    buffer = io.BytesIO()
    # optimize=True is deterministic in zlib; no timestamp chunk is written.
    frame.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


def build_ico(frames: dict[int, bytes]) -> bytes:
    """Assemble a multi-size ICO whose frames ARE the shipped PNG bytes.

    PNG-compressed ICO frames (Vista+) are what the previous package already
    used, so the container format is unchanged. Building it by hand — rather
    than re-encoding through Pillow's ICO writer — guarantees favicon.ico and
    favicon-N.png are the identical image at every size.
    """
    order = sorted(frames)
    header = struct.pack("<HHH", 0, 1, len(order))
    directory = b""
    payload = b""
    offset = len(header) + 16 * len(order)
    for size in order:
        data = frames[size]
        directory += struct.pack(
            "<BBBBHHII",
            size if size < 256 else 0,  # width  (0 means 256)
            size if size < 256 else 0,  # height
            0,                          # palette size (0 = truecolour)
            0,                          # reserved
            1,                          # colour planes
            32,                         # bits per pixel
            len(data),
            offset,
        )
        payload += data
        offset += len(data)
    return header + directory + payload


def occupancy(png: bytes) -> str:
    image = Image.open(io.BytesIO(png)).convert("RGBA")
    width, height = image.size
    # A perceptual threshold: LANCZOS leaves a faint halo whose alpha is
    # visually nothing, and measuring at alpha>0 would overstate the artwork.
    box = alpha_bbox(image, threshold=8)
    art_w, art_h = box[2] - box[0], box[3] - box[1]
    return (f"{art_w}x{art_h} of {width}x{height} "
            f"({art_w / width * 100:.0f}% W, {art_h / height * 100:.0f}% H)")


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true", help="verify only")
    args = parser.parse_args()

    masters = {k: Image.open(p).convert("RGBA") for k, p in MASTERS.items()}

    # Both surfaces are the same geometry in different colours. Cropping them
    # to a SHARED box keeps that true: were one master ever redrawn slightly
    # larger, an independent crop would silently desynchronize the two.
    boxes = [alpha_bbox(image) for image in masters.values()]
    shared = (
        min(b[0] for b in boxes), min(b[1] for b in boxes),
        max(b[2] for b in boxes), max(b[3] for b in boxes),
    )
    canvases = {k: compact_master(image, shared) for k, image in masters.items()}

    outputs: dict[Path, bytes] = {}
    for surface, canvas in canvases.items():
        for size in MARK_SIZES:
            outputs[RUNTIME / "web" / f"nubarca-mark-flat-on-{surface}-{size}.png"] = \
                render(canvas, size)

    favicon_canvas = canvases[FAVICON_SURFACE]
    ico_frames: dict[int, bytes] = {}
    for size in FAVICON_SIZES:
        png = render(favicon_canvas, size)
        outputs[RUNTIME / "favicon" / f"favicon-{size}.png"] = png
        if size in ICO_SIZES:
            ico_frames[size] = png
    outputs[RUNTIME / "favicon" / "favicon.ico"] = build_ico(ico_frames)
    # The 1024 favicon source stays what its name claims: the artwork the
    # favicon family is derived from, now in its compact form.
    outputs[RUNTIME / "favicon" / "favicon-source-1024.png"] = render(favicon_canvas, 1024)

    art_w, art_h = shared[2] - shared[0], shared[3] - shared[1]
    side = next(iter(canvases.values())).width
    print(f"approved artwork      {art_w}x{art_h} inside a {masters['dark'].width}px master "
          f"({art_w / masters['dark'].width * 100:.1f}% W, "
          f"{art_h / masters['dark'].height * 100:.1f}% H)")
    print(f"compact canvas        {side}x{side} "
          f"({art_w / side * 100:.1f}% W, {art_h / side * 100:.1f}% H)")
    print(f"favicon 16 occupancy  {occupancy(outputs[RUNTIME / 'favicon' / 'favicon-16.png'])}")

    if args.check:
        stale = [p for p, data in outputs.items()
                 if not p.exists() or p.read_bytes() != data]
        if stale:
            for path in stale:
                print(f"  - not the compact derivative: {path.relative_to(ROOT)}",
                      file=sys.stderr)
            print(f"\n{len(stale)} stale file(s).", file=sys.stderr)
            return 1
        print(f"compact derivatives current: {len(outputs)} files")
        return 0

    for path, data in outputs.items():
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(data)
    print(f"wrote {len(outputs)} compact derivatives")
    for path in sorted(outputs):
        print(f"  {hashlib.sha256(outputs[path]).hexdigest()[:16]}  "
              f"{path.relative_to(ROOT)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

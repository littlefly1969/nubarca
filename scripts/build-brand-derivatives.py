#!/usr/bin/env python3
"""Generate the approved RUNTIME DERIVATIVES of the NubArca brand package.

A derivative here is a reframing, never a redrawing. The transformation is
purely geometric and raster: measure the artwork's own bounds, scale the WHOLE
source proportionally, and place it on the target canvas. Nothing is recoloured,
tinted, re-typeset, stretched or given an effect, and no artwork is authored.

Currently one derivative:

    runtime/web/nubarca-wordmark-on-light.png
      -> runtime/web/nubarca-wordmark-on-light-480w.png

WHY IT EXISTS. The approved on-light wordmark is a 1516x1024 file in which the
lockup occupies 77.24% of the width; the on-dark compact rendition is 480x135
with the lockup at 98.33%. Shipping the large file to a phone costs 1.9 MB to
draw a 200 px logo, and forces every consumer to divide by a per-theme ratio to
size the same lockup. This rendition puts the light artwork into the SAME
compact frame as the on-dark one, so the two themes share one visible geometry.

WHAT THE FRAMING COSTS, stated rather than hidden: both approved files carry a
faint halo (alpha 1-8, under 3% opacity) around the lockup. The on-dark compact
rendition already confines that halo to a 2 px margin, which is the author's own
framing decision for this frame. Applying the same frame to the light artwork
clips its halo the same way. Everything at drawing opacity is preserved intact.

Usage:
    build-brand-derivatives.py            regenerate in place
    build-brand-derivatives.py --check    fail if a committed file differs
"""

from __future__ import annotations

import argparse
import hashlib
import io
import sys
from pathlib import Path

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / "assets/brand/nubarca/runtime/web"

# Alpha at or below this is halo rather than drawing: it is under 3% opacity and
# invisible on any surface. The bounds of the DRAWING are measured above it.
INK_ALPHA_THRESHOLD = 8


def ink_bounds(image: Image.Image) -> tuple[int, int, int, int]:
    """(left, top, right, bottom) of the drawn artwork, halo excluded."""
    alpha = image.getchannel("A")
    mask = alpha.point(lambda v: 255 if v > INK_ALPHA_THRESHOLD else 0)
    bounds = mask.getbbox()
    if bounds is None:
        raise SystemExit("image carries no artwork")
    return bounds


def compact_on_light(source: Image.Image, reference: Image.Image, size: tuple[int, int]) -> Image.Image:
    """Reframe `source` so its artwork matches `reference`'s geometry on `size`."""
    src_left, src_top, src_right, src_bottom = ink_bounds(source)
    ref_left, ref_top, ref_right, _ = ink_bounds(reference)

    # ONE scale factor for both axes: the artwork's proportions are not touched.
    scale = (ref_right - ref_left) / (src_right - src_left)
    scaled = source.resize(
        (round(source.width * scale), round(source.height * scale)),
        Image.LANCZOS,
    )

    # Place the scaled artwork so its own bounds land where the reference's do.
    offset_x = ref_left - round(src_left * scale)
    offset_y = ref_top - round(src_top * scale)
    canvas = Image.new("RGBA", size, (0, 0, 0, 0))
    canvas.alpha_composite(scaled, dest=(max(offset_x, 0), max(offset_y, 0)),
                           source=(max(-offset_x, 0), max(-offset_y, 0)))
    return canvas


def render() -> bytes:
    source = Image.open(WEB / "nubarca-wordmark-on-light.png").convert("RGBA")
    reference = Image.open(WEB / "nubarca-wordmark-on-dark-480w.png").convert("RGBA")
    out = compact_on_light(source, reference, reference.size)
    buffer = io.BytesIO()
    # No metadata, no timestamp: the same input must give the same bytes.
    out.save(buffer, format="PNG", optimize=True)
    return buffer.getvalue()


TARGET = WEB / "nubarca-wordmark-on-light-480w.png"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    args = parser.parse_args()

    rendered = render()
    digest = hashlib.sha256(rendered).hexdigest()

    if args.check:
        if not TARGET.exists():
            print(f"missing derivative: {TARGET.relative_to(ROOT)}", file=sys.stderr)
            return 1
        committed = TARGET.read_bytes()
        if committed != rendered:
            print(
                f"{TARGET.relative_to(ROOT)} is not reproducible from its source\n"
                f"  committed {hashlib.sha256(committed).hexdigest()}\n"
                f"  rendered  {digest}\n"
                f"  (Pillow {Image.__version__}; a different encoder version can "
                f"change the bytes without changing the picture)",
                file=sys.stderr,
            )
            return 1
        print(f"brand derivative reproducible: {TARGET.name} {digest[:16]}…")
        return 0

    TARGET.write_bytes(rendered)
    image = Image.open(TARGET)
    print(f"wrote {TARGET.relative_to(ROOT)} {image.width}x{image.height} "
          f"{len(rendered)} bytes sha256 {digest}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

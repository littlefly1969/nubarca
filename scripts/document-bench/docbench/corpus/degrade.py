"""Turn a clean page render into a scan, a phone photo or a low-DPI fax.

Every random choice comes from a generator seeded by (doc, page, variant), so
the same inputs give the same pixels. Pillow + numpy only: no OpenCV, so the
container stays small and the operations stay auditable.
"""

from __future__ import annotations

import hashlib

import numpy as np
from PIL import Image, ImageFilter

VARIANTS = ("clean", "scan", "photo", "lowdpi")
# DPI each variant is rendered at BEFORE degradation.
RENDER_DPI = {"clean": 200, "scan": 150, "photo": 170, "lowdpi": 96}


def rng_for(doc_id: str, page: int, variant: str) -> np.random.Generator:
    seed = int.from_bytes(hashlib.sha256(f"{doc_id}:{page}:{variant}".encode()).digest()[:8], "big")
    return np.random.default_rng(seed)


def _noise(img: Image.Image, rng: np.random.Generator, sigma: float) -> Image.Image:
    arr = np.asarray(img, dtype=np.float32)
    arr = arr + rng.normal(0.0, sigma, arr.shape).astype(np.float32)
    return Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), img.mode)


def scan(img: Image.Image, rng: np.random.Generator) -> Image.Image:
    """A flatbed scan: grey, slightly skewed, slightly soft, speckled."""
    g = img.convert("L")
    angle = float(rng.uniform(0.4, 1.6) * rng.choice([-1, 1]))
    g = g.rotate(angle, resample=Image.BICUBIC, expand=False, fillcolor=255)
    g = g.filter(ImageFilter.GaussianBlur(radius=0.6))
    arr = np.asarray(g, dtype=np.float32)
    arr = 235 - (235 - 25) * (1 - arr / 255.0)  # lower contrast, grey paper
    specks = rng.random(arr.shape) < 0.0008
    arr[specks] = rng.uniform(40, 120, specks.sum())
    g = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "L")
    return _noise(g, rng, 7.0)


def _perspective_coeffs(src, dst):
    """Coefficients for Image.transform(PERSPECTIVE) mapping dst -> src."""
    matrix = []
    for (x, y), (u, v) in zip(dst, src):
        matrix.append([x, y, 1, 0, 0, 0, -u * x, -u * y])
        matrix.append([0, 0, 0, x, y, 1, -v * x, -v * y])
    a = np.array(matrix, dtype=np.float64)
    b = np.array(src, dtype=np.float64).reshape(8)
    return np.linalg.solve(a, b).tolist()


def photo(img: Image.Image, rng: np.random.Generator) -> Image.Image:
    """A phone photo of a sheet on a desk: perspective, tilt, uneven light, blur, JPEG."""
    w, h = img.size
    pad = int(0.08 * max(w, h))
    canvas = Image.new("RGB", (w + 2 * pad, h + 2 * pad), (118, 96, 74))
    canvas.paste(img.convert("RGB"), (pad, pad))
    W, H = canvas.size
    j = lambda: float(rng.uniform(0.02, 0.06))  # noqa: E731
    src = [(0, 0), (W, 0), (W, H), (0, H)]
    dst = [(W * j(), H * j()), (W * (1 - j()), H * j()),
           (W * (1 - j()), H * (1 - j())), (W * j(), H * (1 - j()))]
    canvas = canvas.transform((W, H), Image.PERSPECTIVE, _perspective_coeffs(src, dst),
                              resample=Image.BICUBIC, fillcolor=(90, 72, 56))
    canvas = canvas.rotate(float(rng.uniform(-3.0, 3.0)), resample=Image.BICUBIC,
                           fillcolor=(90, 72, 56))
    # Light falls off from one corner.
    yy, xx = np.mgrid[0:H, 0:W].astype(np.float32)
    cx, cy = rng.uniform(0, W), rng.uniform(0, H)
    dist = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / np.hypot(W, H)
    light = (1.0 - 0.35 * dist)[..., None]
    arr = np.asarray(canvas, dtype=np.float32) * light
    canvas = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), "RGB")
    canvas = canvas.filter(ImageFilter.GaussianBlur(radius=float(rng.uniform(0.8, 1.3))))
    canvas = _noise(canvas, rng, 5.0)
    longest = 1800
    scale = longest / max(canvas.size)
    if scale < 1:
        canvas = canvas.resize((round(canvas.width * scale), round(canvas.height * scale)),
                               Image.LANCZOS)
    return canvas


def lowdpi(img: Image.Image, rng: np.random.Generator) -> Image.Image:
    """A fax-like page: 96 dpi, binarised with a slightly noisy threshold."""
    g = _noise(img.convert("L"), rng, 10.0)
    threshold = float(rng.uniform(150, 175))
    return g.point(lambda v: 255 if v > threshold else 0, mode="L")


def degrade(img: Image.Image, doc_id: str, page: int, variant: str) -> Image.Image:
    if variant == "clean":
        return img.convert("RGB")
    rng = rng_for(doc_id, page, variant)
    return {"scan": scan, "photo": photo, "lowdpi": lowdpi}[variant](img, rng)

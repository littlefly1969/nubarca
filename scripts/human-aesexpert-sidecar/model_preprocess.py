"""Official HumanAesExpert / InternVL preprocessing (448x448 dynamic tiling).

This mirrors the checkpoint's own image transform so the `human-aesexpert-
official-v1` profile preserves the paper/model behavior EXACTLY. Any reduced
variant (fewer tiles / lower resolution) MUST use a different profile key and
its own branch here — never silently weaken official-v1.

Kept as a separate module so the transform is unit-testable without the model.
"""
from PIL import Image

IMAGENET_MEAN = (0.485, 0.456, 0.406)
IMAGENET_STD = (0.229, 0.224, 0.225)
INPUT_SIZE = 448
OFFICIAL_MAX_TILES = 12


def _transform(image: "Image.Image"):
    import torchvision.transforms as T

    return T.Compose(
        [
            T.Lambda(lambda img: img.convert("RGB") if img.mode != "RGB" else img),
            T.Resize((INPUT_SIZE, INPUT_SIZE), interpolation=T.InterpolationMode.BICUBIC),
            T.ToTensor(),
            T.Normalize(mean=IMAGENET_MEAN, std=IMAGENET_STD),
        ]
    )


def _find_closest_aspect_ratio(aspect_ratio, target_ratios, width, height, image_size):
    best_diff = float("inf")
    best = (1, 1)
    area = width * height
    for ratio in target_ratios:
        target = ratio[0] / ratio[1]
        diff = abs(aspect_ratio - target)
        if diff < best_diff or (
            diff == best_diff and area > 0.5 * image_size * image_size * ratio[0] * ratio[1]
        ):
            best_diff = diff
            best = ratio
    return best


def _dynamic_preprocess(image, min_num=1, max_num=OFFICIAL_MAX_TILES, image_size=INPUT_SIZE, use_thumbnail=True):
    width, height = image.size
    aspect_ratio = width / height
    target_ratios = sorted(
        {
            (i, j)
            for n in range(min_num, max_num + 1)
            for i in range(1, n + 1)
            for j in range(1, n + 1)
            if min_num <= i * j <= max_num
        },
        key=lambda x: x[0] * x[1],
    )
    ratio = _find_closest_aspect_ratio(aspect_ratio, target_ratios, width, height, image_size)
    target_width = image_size * ratio[0]
    target_height = image_size * ratio[1]
    blocks = ratio[0] * ratio[1]
    resized = image.resize((target_width, target_height))
    tiles = []
    cols = target_width // image_size
    for i in range(blocks):
        box = (
            (i % cols) * image_size,
            (i // cols) * image_size,
            ((i % cols) + 1) * image_size,
            ((i // cols) + 1) * image_size,
        )
        tiles.append(resized.crop(box))
    if use_thumbnail and len(tiles) != 1:
        tiles.append(image.resize((image_size, image_size)))
    return tiles


def build_pixel_values(image: "Image.Image", preprocessing_profile: str):
    """Return a float tensor [n_tiles, 3, 448, 448] for the model."""
    import torch

    if preprocessing_profile == "human-aesexpert-controlled-v1":
        # A reduced, faster profile (single 448 tile, no dynamic tiling). This is
        # DELIBERATELY not equivalent to the official pipeline and is recorded
        # under its own key so a run is never mistaken for official behavior.
        max_tiles = 1
        use_thumbnail = False
    else:
        # official-v1 (default): the checkpoint's own 448 dynamic tiling.
        max_tiles = OFFICIAL_MAX_TILES
        use_thumbnail = True

    tiles = _dynamic_preprocess(image, max_num=max_tiles, use_thumbnail=use_thumbnail)
    transform = _transform(image)
    pixel_values = torch.stack([transform(t) for t in tiles])
    return pixel_values.to(torch.float32)

#!/usr/bin/env python3
"""Export SAM 2.1 tiny for "tap a subject" (Ai:SubjectCutout:PointSelection).

Weights are never committed. Give this script the official Meta checkpoint
(sam2.1_hiera_tiny.pt, Apache-2.0) and a checkout of facebookresearch/sam2 at
the pinned commit; it writes the two files OnnxSubjectPointModel loads:

  <output>/sam2.1-tiny/encoder.onnx
      image [1,3,1024,1024] (photo stretched, ImageNet mean/std)
      -> image_embed [1,256,64,64], high_res_feats_0 [1,32,256,256],
         high_res_feats_1 [1,64,128,128]
  <output>/sam2.1-tiny/decoder.onnx
      image_embed, high_res_feats_0, high_res_feats_1,
      point_coords float [1,N,2] (pixels of the 1024 square),
      point_labels int32 [1,N]
      -> low_res_masks [1,3,256,256] (logits), iou [1,3]

Image mode only: no video memory. Requires a controlled model-preparation
environment with torch, hydra-core, iopath and onnx installed. See
docs/model-deployment/subject-cutout.md.
"""

from __future__ import annotations

import argparse
import hashlib
import sys
from pathlib import Path

import torch

# The sam2 code the export was validated against. The model graph is defined by
# this code, so bump it only after re-running the opt-in model test.
SAM2_COMMIT = "2b90b9f5ceec907a1c18123530e92e794ad901a4"
CONFIG = "configs/sam2.1/sam2.1_hiera_t.yaml"
MODEL_DIR = "sam2.1-tiny"
OPSET = 17


class Encoder(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, image: torch.Tensor):
        backbone = self.model.forward_image(image)
        _, feats, _, _ = self.model._prepare_backbone_features(backbone)
        # Image mode: no memory, so the "no memory" embedding is added, exactly
        # as SAM2ImagePredictor.set_image does.
        feats[-1] = feats[-1] + self.model.no_mem_embed
        sizes = [(256, 256), (128, 128), (64, 64)]
        maps = [
            feat.permute(1, 2, 0).reshape(1, -1, *size)
            for feat, size in zip(feats[::-1], sizes[::-1])
        ][::-1]
        return maps[2], maps[0], maps[1]


class Decoder(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, image_embed, high_res_feats_0, high_res_feats_1, point_coords, point_labels):
        sparse, dense = self.model.sam_prompt_encoder(
            points=(point_coords, point_labels), boxes=None, masks=None
        )
        low_res_masks, iou, _, _ = self.model.sam_mask_decoder(
            image_embeddings=image_embed,
            image_pe=self.model.sam_prompt_encoder.get_dense_pe(),
            sparse_prompt_embeddings=sparse,
            dense_prompt_embeddings=dense,
            multimask_output=True,
            repeat_image=False,
            high_res_features=[high_res_feats_0, high_res_feats_1],
        )
        return low_res_masks, iou


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--sam2", required=True, type=Path, help="facebookresearch/sam2 checkout")
    parser.add_argument("--checkpoint", required=True, type=Path, help="sam2.1_hiera_tiny.pt")
    parser.add_argument("--output", required=True, type=Path, help="Ai:Onnx:ModelDir")
    args = parser.parse_args()

    sys.path.insert(0, str(args.sam2.resolve()))
    from hydra import initialize_config_module
    from hydra.core.global_hydra import GlobalHydra
    from sam2.build_sam import build_sam2

    GlobalHydra.instance().clear()
    initialize_config_module("sam2", version_base="1.2")
    # build_sam2 loads the checkpoint with weights_only=True.
    model = build_sam2(CONFIG, str(args.checkpoint), device="cpu")

    out = args.output / MODEL_DIR
    out.mkdir(parents=True, exist_ok=True)
    with torch.no_grad():
        image = torch.randn(1, 3, 1024, 1024)
        embed, feats0, feats1 = Encoder(model)(image)
        torch.onnx.export(
            Encoder(model), image, out / "encoder.onnx",
            input_names=["image"],
            output_names=["image_embed", "high_res_feats_0", "high_res_feats_1"],
            opset_version=OPSET, dynamo=False,
        )
        coords = torch.tensor([[[512.0, 512.0]]])
        labels = torch.tensor([[1]], dtype=torch.int32)
        torch.onnx.export(
            Decoder(model), (embed, feats0, feats1, coords, labels), out / "decoder.onnx",
            input_names=["image_embed", "high_res_feats_0", "high_res_feats_1", "point_coords", "point_labels"],
            output_names=["low_res_masks", "iou"],
            dynamic_axes={"point_coords": {1: "points"}, "point_labels": {1: "points"}},
            opset_version=OPSET, dynamo=False,
        )

    print(f"checkpoint sha256 {sha256(args.checkpoint)}")
    for name in ("encoder.onnx", "decoder.onnx"):
        path = out / name
        print(f"{path} {path.stat().st_size} bytes sha256 {sha256(path)}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

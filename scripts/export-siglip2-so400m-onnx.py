#!/usr/bin/env python3
"""Export the one approved NubArca multimodal photo profile.

Weights are downloaded by Hugging Face into its external cache and are never
committed. Output layout matches OnnxImageModels exactly:

  <output>/siglip2-so400m-patch14-384/model.onnx       (image_embeds)
  <output>/siglip2-so400m-patch14-384/text_model.onnx  (text_embeds)
  <output>/siglip2-so400m-patch14-384/tokenizer.json

Requires a controlled model-preparation environment with torch, transformers,
onnx and safetensors installed. Run scripts/validate-siglip2-onnx.py afterwards.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import torch
from transformers import AutoModel, AutoProcessor


CHECKPOINT = "google/siglip2-so400m-patch14-384"
# Pin the checkpoint so image/text towers can never be exported from different
# moving revisions. Bump only through a reviewed new profile/version.
REVISION = "c65677ac77ca25276518923f7c58cbf5d81ea602"
MODEL_DIR = "siglip2-so400m-patch14-384"
SEQ_LEN = 64


class ImageTower(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, pixel_values: torch.Tensor) -> torch.Tensor:
        return self.model.get_image_features(pixel_values=pixel_values)


class TextTower(torch.nn.Module):
    def __init__(self, model: torch.nn.Module) -> None:
        super().__init__()
        self.model = model

    def forward(self, input_ids: torch.Tensor, attention_mask: torch.Tensor) -> torch.Tensor:
        return self.model.get_text_features(
            input_ids=input_ids, attention_mask=attention_mask
        )


def export(output_root: Path, opset: int) -> None:
    target = output_root / MODEL_DIR
    target.mkdir(parents=True, exist_ok=True)

    model = AutoModel.from_pretrained(
        CHECKPOINT, revision=REVISION, torch_dtype=torch.float32
    )
    model.eval()
    processor = AutoProcessor.from_pretrained(CHECKPOINT, revision=REVISION)
    tokenizer = processor.tokenizer

    # Persist the checkpoint tokenizer plus its fixed-length policy in
    # tokenizer.json. The .NET runtime refuses any asset that does not produce
    # exactly 64 ids.
    tokenizer.backend_tokenizer.enable_truncation(max_length=SEQ_LEN)
    tokenizer.backend_tokenizer.enable_padding(
        length=SEQ_LEN,
        direction="right",
        pad_id=tokenizer.pad_token_id,
        pad_type_id=0,
        pad_token=tokenizer.pad_token,
    )
    tokenizer.backend_tokenizer.save(str(target / "tokenizer.json"))

    image = torch.zeros((1, 3, 384, 384), dtype=torch.float32)
    text = tokenizer(
        ["una foto di un cane nero sulla neve"],
        padding="max_length",
        truncation=True,
        max_length=SEQ_LEN,
        return_attention_mask=True,
        return_tensors="pt",
    )
    if "attention_mask" not in text:
        raise RuntimeError("Checkpoint tokenizer did not return attention_mask")
    # SigLIP2 FixRes uses fixed-length padding without masking padding tokens.
    # AutoProcessor calls get_text_features without attention_mask; keep the
    # graph's stable two-input contract but export/probe it with all positions
    # attended. The .NET runtime follows the same invariant.
    text_attention_mask = torch.ones_like(text["input_ids"])

    with torch.inference_mode():
        torch.onnx.export(
            ImageTower(model).eval(),
            (image,),
            target / "model.onnx",
            input_names=["pixel_values"],
            output_names=["image_embeds"],
            dynamic_axes={"pixel_values": {0: "batch"}, "image_embeds": {0: "batch"}},
            opset_version=opset,
            do_constant_folding=True,
            external_data=True,
        )
        torch.onnx.export(
            TextTower(model).eval(),
            (text["input_ids"], text_attention_mask),
            target / "text_model.onnx",
            input_names=["input_ids", "attention_mask"],
            output_names=["text_embeds"],
            dynamic_axes={
                "input_ids": {0: "batch"},
                "attention_mask": {0: "batch"},
                "text_embeds": {0: "batch"},
            },
            opset_version=opset,
            do_constant_folding=True,
            external_data=True,
        )


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--opset", type=int, default=17)
    args = parser.parse_args()
    export(args.output.resolve(), args.opset)


if __name__ == "__main__":
    main()

"""Visual page encoders. Each returns, per page, either one vector (dense) or a
matrix of token vectors (late interaction), plus the same for queries.

Late interaction is scored with MEAN-MaxSim (NeoMME's `mean_maxsim`) or
SUM-MaxSim (ColPali convention), as each model card specifies; the choice is
recorded in the manifest. Every index-cost number counts float32 bytes, the
storage NubArca uses today, with float16 shown alongside.
"""

from __future__ import annotations

import time
from pathlib import Path

import numpy as np
from PIL import Image

from .encoders import Unavailable, l2


class VisualModel:
    name = ""
    lock = ""
    heads: tuple[str, ...] = ("dense",)
    late_reduce = "mean"
    load_seconds = 0.0
    info: dict = {}

    def pages(self, images: list[Image.Image]) -> dict[str, list[np.ndarray]]:
        raise NotImplementedError

    def query(self, text: str) -> dict[str, np.ndarray]:
        raise NotImplementedError


def _torch(threads: int):
    try:
        import torch
    except ImportError as exc:
        raise Unavailable(f"torch not installed: {exc}") from exc
    torch.set_num_threads(threads)
    return torch


def _path(models_dir: Path, lock: str) -> Path:
    path = models_dir / lock
    if not path.exists():
        raise Unavailable(f"weights not found at {path}")
    return path


class SigLip2(VisualModel):
    """NubArca's photo/page baseline: one 1152-d vector per page at 384 px."""
    name, lock = "siglip2-so400m", "siglip2-so400m"

    def __init__(self, models_dir: Path, threads: int):
        torch = _torch(threads)
        path = _path(models_dir, self.lock)
        from transformers import AutoModel, AutoProcessor
        start = time.perf_counter()
        self.model = AutoModel.from_pretrained(str(path), local_files_only=True,
                                               torch_dtype=torch.float32).eval()
        self.processor = AutoProcessor.from_pretrained(str(path), local_files_only=True)
        self.load_seconds = time.perf_counter() - start
        self.torch = torch
        self.info = {"precision": "fp32", "image_size": 384, "text_tokens": 64,
                     "text_padding": "max_length 64 (as NubArca's SigLIP2 profile)"}

    def pages(self, images):
        with self.torch.inference_mode():
            batch = self.processor(images=[i.convert("RGB") for i in images], return_tensors="pt")
            feats = self.model.get_image_features(**batch)
        return {"dense": list(l2(feats.float().numpy()))}

    def query(self, text):
        with self.torch.inference_mode():
            batch = self.processor(text=[text], padding="max_length", max_length=64,
                                   truncation=True, return_tensors="pt")
            feats = self.model.get_text_features(input_ids=batch["input_ids"])
        return {"dense": l2(feats.float().numpy())[0]}


class NeoMme(VisualModel):
    """NeoMME-260M-Retriever via transformers: dense (1024, MRL) and late (128/token)."""
    name, lock = "neomme-260m", "neomme-260m"
    heads = ("dense", "late")
    late_reduce = "mean"

    def __init__(self, models_dir: Path, threads: int):
        torch = _torch(threads)
        path = _path(models_dir, self.lock)
        try:
            from transformers import NeoMMEForRetrieval, NeoMMEProcessor
        except ImportError as exc:
            raise Unavailable(f"transformers has no NeoMME classes: {exc}") from exc
        start = time.perf_counter()
        self.processor = NeoMMEProcessor.from_pretrained(str(path), local_files_only=True)
        self.model = NeoMMEForRetrieval.from_pretrained(str(path), local_files_only=True,
                                                        torch_dtype=torch.float32).eval()
        self.load_seconds = time.perf_counter() - start
        self.torch = torch
        self.info = {"precision": "fp32", "patch": 32, "late_dim": 128, "dense_dim": 1024,
                     "scoring_late": "mean MaxSim (model card mean_maxsim)",
                     "scoring_dense": "cosine"}

    def _encode(self, messages, task):
        batch = self.processor.apply_chat_template(messages, task=task, tokenize=True,
                                                   return_dict=True, return_tensors="pt",
                                                   processor_kwargs={"padding": "longest"})
        with self.torch.inference_mode():
            out = self.model(**batch)
        mask = batch["attention_mask"].bool().numpy()
        late = out.embeddings.float().numpy()
        dense = l2(out.dense_embeddings.float().numpy())
        return [late[i][mask[i]] for i in range(late.shape[0])], dense

    def pages(self, images):
        late, dense = self._encode([[{"role": "user", "content": [
            {"type": "image", "image": i.convert("RGB")}]}] for i in images], "document")
        return {"dense": list(dense), "late": late}

    def query(self, text):
        late, dense = self._encode([[{"role": "user", "content": text}]], "query")
        return {"dense": dense[0], "late": late[0]}


class StMultiVector(VisualModel):
    """sentence-transformers MultiVectorEncoder (ST >= 6) for ST-packaged late models."""
    heads = ("late",)

    def __init__(self, name: str, lock: str, reduce: str, models_dir: Path, threads: int):
        self.name, self.lock, self.late_reduce = name, lock, reduce
        _torch(threads)
        path = _path(models_dir, lock)
        try:
            from sentence_transformers import MultiVectorEncoder
        except ImportError as exc:
            raise Unavailable(f"sentence-transformers MultiVectorEncoder missing: {exc}") from exc
        start = time.perf_counter()
        try:
            self.model = MultiVectorEncoder(str(path), device="cpu", local_files_only=True)
        except Exception as exc:  # noqa: BLE001 - e.g. missing fla/causal-conv1d kernels
            raise Unavailable(f"load failed: {type(exc).__name__}: {str(exc)[:300]}") from exc
        self.load_seconds = time.perf_counter() - start
        self.info = {"precision": "fp32", "runtime": "sentence-transformers MultiVectorEncoder",
                     "scoring_late": f"{reduce} MaxSim"}

    @staticmethod
    def _matrix(x) -> np.ndarray:
        arr = x.float().numpy() if hasattr(x, "float") else np.asarray(x, dtype=np.float32)
        return arr[np.abs(arr).sum(axis=1) > 0]  # drop masked (zero) rows

    def pages(self, images):
        out = self.model.encode_document([i.convert("RGB") for i in images])
        return {"late": [self._matrix(x) for x in out]}

    def query(self, text):
        return {"late": self._matrix(self.model.encode_query([text])[0])}


class StDense(VisualModel):
    """A single-vector multimodal sentence-transformers model (Qwen3-VL-Embedding)."""

    def __init__(self, name: str, lock: str, prompt: str, models_dir: Path, threads: int):
        self.name, self.lock, self.prompt = name, lock, prompt
        _torch(threads)
        path = _path(models_dir, lock)
        try:
            from sentence_transformers import SentenceTransformer
        except ImportError as exc:
            raise Unavailable(f"sentence-transformers missing: {exc}") from exc
        start = time.perf_counter()
        try:
            self.model = SentenceTransformer(str(path), device="cpu", local_files_only=True)
        except Exception as exc:  # noqa: BLE001
            raise Unavailable(f"load failed: {type(exc).__name__}: {str(exc)[:300]}") from exc
        self.load_seconds = time.perf_counter() - start
        self.info = {"precision": "fp32", "query_instruction": prompt}

    def pages(self, images):
        out = self.model.encode([i.convert("RGB") for i in images], normalize_embeddings=True,
                                convert_to_numpy=True, show_progress_bar=False)
        return {"dense": list(out.astype(np.float32))}

    def query(self, text):
        out = self.model.encode([text], prompt=self.prompt, normalize_embeddings=True,
                                convert_to_numpy=True, show_progress_bar=False)
        return {"dense": out[0].astype(np.float32)}


VISUAL_MODELS = ("siglip2-so400m", "neomme-260m", "neomme-260m-st-late", "vultron-flash-0.8b",
                 "qwen3-vl-emb-2b")


def load_visual(name: str, models_dir: Path, threads: int) -> VisualModel:
    if name == "siglip2-so400m":
        return SigLip2(models_dir, threads)
    if name == "neomme-260m":
        return NeoMme(models_dir, threads)
    if name == "neomme-260m-st-late":
        return StMultiVector(name, "neomme-260m-st-late", "mean", models_dir, threads)
    if name == "vultron-flash-0.8b":
        return StMultiVector(name, "vultron-flash-0.8b", "sum", models_dir, threads)
    if name == "qwen3-vl-emb-2b":
        return StDense(name, "qwen3-vl-emb-2b",
                       "Retrieve the document page that answers the user's question.",
                       models_dir, threads)
    raise SystemExit(f"unknown visual model {name!r}; known: {', '.join(VISUAL_MODELS)}")


def maxsim(query: np.ndarray, page: np.ndarray, reduce: str) -> float:
    q = l2(query)
    d = l2(page)
    best = (q @ d.T).max(axis=1)
    return float(best.mean() if reduce == "mean" else best.sum())

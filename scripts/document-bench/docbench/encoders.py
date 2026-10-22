"""Text encoders: a sentence-transformers REFERENCE and raw ONNX Runtime /
OpenVINO paths that do their own tokenisation and pooling, the way NubArca's
.NET provider does. Comparing the two is the parity check.

Every model carries its OWN query/passage convention. Reusing E5's
"query: "/"passage: " for another model silently degrades it, so the strings
live here, per model, and are written into the run manifest.
"""

from __future__ import annotations

import json
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

import numpy as np

QWEN3_INSTRUCTION = ("Given a question, retrieve passages from the user's personal documents "
                     "that answer the question")


@dataclass(frozen=True)
class TextModel:
    key: str
    lock: str  # entry in models.lock.json holding the reference weights
    pooling: str  # mean | cls | last
    query_prefix: str
    passage_prefix: str
    max_tokens: int
    dim: int
    padding: str = "right"
    matryoshka: tuple[int, ...] = ()
    # runtime -> (lock entry, model file, tokenizer file)
    files: dict = field(default_factory=dict)
    notes: str = ""


TEXT_MODELS: dict[str, TextModel] = {
    "e5-small": TextModel(
        "e5-small", "e5-small", "mean", "query: ", "passage: ", 512, 384,
        files={"ort": ("e5-small", "onnx/model.onnx", "onnx/tokenizer.json"),
               "ort-int8": ("e5-small", "onnx/model_qint8_avx512_vnni.onnx", "onnx/tokenizer.json"),
               "ov": ("e5-small", "onnx/model.onnx", "onnx/tokenizer.json")},
        notes="NubArca's current profile (rag-text-multilingual-e5-small-v1). The int8 file is "
              "built for AVX512-VNNI; on a CPU without it ONNX Runtime falls back to slower kernels."),
    "qwen3-emb-0.6b": TextModel(
        "qwen3-emb-0.6b", "qwen3-emb-0.6b", "last",
        f"Instruct: {QWEN3_INSTRUCTION}\nQuery:", "", 1024, 1024, padding="left",
        matryoshka=(768, 512),
        files={"ov-int8": ("qwen3-emb-0.6b-ov-int8", "openvino_model.xml", "tokenizer.json"),
               "ort": ("qwen3-emb-0.6b-onnx", "onnx/model.onnx", "tokenizer.json")},
        notes="Last-token pooling with LEFT padding; instruction on queries only, documents raw "
              "(model card). Matryoshka dims are truncation of the 1024-d output + L2."),
    "granite-311m": TextModel(
        "granite-311m", "granite-311m", "cls", "", "", 512, 768, matryoshka=(512, 384, 256),
        files={"ort": ("granite-311m", "onnx/model.onnx", "tokenizer.json"),
               "ort-int8": ("granite-311m", "onnx/model_quint8_avx2.onnx", "tokenizer.json"),
               "ov": ("granite-311m", "openvino_model.xml", "tokenizer.json"),
               "ov-int8": ("granite-311m", "openvino/openvino_model_qint8_quantized.xml",
                           "tokenizer.json")},
        notes="CLS pooling + L2, no prefixes (model card). 32k context; benchmarked at 512."),
    "granite-97m": TextModel(
        "granite-97m", "granite-97m", "cls", "", "", 512, 384,
        files={"ort": ("granite-97m", "onnx/model.onnx", "tokenizer.json"),
               "ort-int8": ("granite-97m", "onnx/model_quint8_avx2.onnx", "tokenizer.json"),
               "ov": ("granite-97m", "openvino_model.xml", "tokenizer.json"),
               "ov-int8": ("granite-97m", "openvino/openvino_model_qint8_quantized.xml",
                           "tokenizer.json")},
        notes="Same 384-d size class as e5-small; CLS pooling, no prefixes."),
    "bge-m3": TextModel(
        "bge-m3", "bge-m3", "cls", "", "", 1024, 1024,
        files={"ort": ("bge-m3", "onnx/model.onnx", "onnx/tokenizer.json")},
        notes="Dense head only (CLS + L2); sparse and ColBERT heads not evaluated."),
}

RUNTIMES = ("st", "ort", "ort-int8", "ov", "ov-int8")


class Unavailable(RuntimeError):
    """The runtime or weights are missing: a recorded skip, never a crash."""


def l2(x: np.ndarray) -> np.ndarray:
    return x / np.maximum(np.linalg.norm(x, axis=1, keepdims=True), 1e-12)


def truncate(x: np.ndarray, dim: int) -> np.ndarray:
    return l2(x[:, :dim]) if dim < x.shape[1] else x


class Encoder:
    model: TextModel
    runtime: str
    load_seconds: float = 0.0
    details: dict

    def encode(self, texts: list[str], kind: str, batch: int) -> np.ndarray:
        raise NotImplementedError

    def describe(self) -> dict:
        return {"model": asdict(self.model), "runtime": self.runtime,
                "load_seconds": self.load_seconds, **self.details}


def _prefix(model: TextModel, kind: str) -> str:
    return model.query_prefix if kind == "query" else model.passage_prefix


class SentenceTransformerEncoder(Encoder):
    def __init__(self, model: TextModel, root: Path, threads: int):
        try:
            import torch
            from sentence_transformers import SentenceTransformer
        except ImportError as exc:
            raise Unavailable(f"sentence-transformers/torch not installed: {exc}") from exc
        path = root / model.lock
        if not path.exists():
            raise Unavailable(f"weights not found at {path}")
        torch.set_num_threads(threads)
        start = time.perf_counter()
        self.st = SentenceTransformer(str(path), device="cpu", local_files_only=True)
        self.st.max_seq_length = model.max_tokens
        self.load_seconds = time.perf_counter() - start
        self.model, self.runtime = model, "st"
        self.details = {"precision": "fp32", "framework": "torch",
                        "torch_threads": torch.get_num_threads(),
                        "st_modules": [type(m).__name__ for m in self.st]}

    def encode(self, texts, kind, batch):
        prefix = _prefix(self.model, kind)
        out = self.st.encode(texts, prompt=prefix or None, batch_size=batch,
                             normalize_embeddings=True, convert_to_numpy=True,
                             show_progress_bar=False)
        return out.astype(np.float32)


class _RawEncoder(Encoder):
    """Own tokenisation, padding and pooling: what a .NET runtime has to reproduce."""

    def __init__(self, model: TextModel, root: Path, runtime: str, threads: int):
        if runtime not in model.files:
            raise Unavailable(f"{model.key} has no {runtime} export in this harness")
        lock, model_file, tokenizer_file = model.files[runtime]
        base = root / lock
        self.path = base / model_file
        tok_path = base / tokenizer_file
        if not self.path.exists():
            raise Unavailable(f"model file not found: {self.path}")
        if not tok_path.exists():
            raise Unavailable(f"tokenizer not found: {tok_path}")
        try:
            from tokenizers import Tokenizer
        except ImportError as exc:
            raise Unavailable(f"tokenizers not installed: {exc}") from exc
        self.tokenizer = Tokenizer.from_file(str(tok_path))
        self.tokenizer.no_padding()
        self.tokenizer.enable_truncation(max_length=model.max_tokens)
        self.pad_id = next((self.tokenizer.token_to_id(t) for t in
                            ("<pad>", "[PAD]", "<|endoftext|>") if self.tokenizer.token_to_id(t)
                            is not None), 0)
        self.model, self.runtime, self.threads = model, runtime, threads
        self.details = {"model_file": model_file, "tokenizer_file": tokenizer_file,
                        "weights_entry": lock, "pad_id": self.pad_id, "padding": model.padding,
                        "pooling": model.pooling,
                        "precision": "int8" if runtime.endswith("int8") else "fp32"}

    def _batch(self, texts: list[str], kind: str) -> dict[str, np.ndarray]:
        prefix = _prefix(self.model, kind)
        encodings = self.tokenizer.encode_batch([prefix + t for t in texts])
        length = max(len(e.ids) for e in encodings)
        ids = np.full((len(texts), length), self.pad_id, dtype=np.int64)
        mask = np.zeros((len(texts), length), dtype=np.int64)
        for i, e in enumerate(encodings):
            n = len(e.ids)
            if self.model.padding == "left":
                ids[i, length - n:] = e.ids
                mask[i, length - n:] = 1
            else:
                ids[i, :n] = e.ids
                mask[i, :n] = 1
        positions = np.clip(np.cumsum(mask, axis=1) - 1, 0, None).astype(np.int64)
        return {"input_ids": ids, "attention_mask": mask,
                "token_type_ids": np.zeros_like(ids), "position_ids": positions}

    def _pool(self, hidden: np.ndarray, mask: np.ndarray) -> np.ndarray:
        if hidden.ndim == 2:
            return l2(hidden.astype(np.float32))
        if self.model.pooling == "cls":
            pooled = hidden[:, 0]
        elif self.model.pooling == "last":
            if self.model.padding == "left":
                pooled = hidden[:, -1]
            else:
                last = mask.sum(axis=1) - 1
                pooled = hidden[np.arange(hidden.shape[0]), last]
        else:
            m = mask[..., None].astype(np.float32)
            pooled = (hidden * m).sum(axis=1) / np.maximum(m.sum(axis=1), 1e-9)
        return l2(pooled.astype(np.float32))

    def encode(self, texts, kind, batch):
        out = []
        for i in range(0, len(texts), batch):
            feed = self._batch(texts[i:i + batch], kind)
            out.append(self._pool(self._run(feed), feed["attention_mask"]))
        return np.concatenate(out, axis=0)


class OrtEncoder(_RawEncoder):
    def __init__(self, model, root, runtime, threads):
        super().__init__(model, root, runtime, threads)
        try:
            import onnxruntime as ort
        except ImportError as exc:
            raise Unavailable(f"onnxruntime not installed: {exc}") from exc
        options = ort.SessionOptions()
        options.intra_op_num_threads = threads
        options.inter_op_num_threads = 1
        start = time.perf_counter()
        self.session = ort.InferenceSession(str(self.path), options,
                                            providers=["CPUExecutionProvider"])
        self.load_seconds = time.perf_counter() - start
        self.inputs = [i.name for i in self.session.get_inputs()]
        outputs = [o.name for o in self.session.get_outputs()]
        self.output = "sentence_embedding" if "sentence_embedding" in outputs else outputs[0]
        self.details.update({"framework": "onnxruntime", "inputs": self.inputs,
                             "output": self.output, "intra_op_threads": threads})

    def _run(self, feed):
        return self.session.run([self.output], {k: feed[k] for k in self.inputs})[0]


class OpenVinoEncoder(_RawEncoder):
    def __init__(self, model, root, runtime, threads):
        super().__init__(model, root, runtime, threads)
        try:
            import openvino as ov
        except ImportError as exc:
            raise Unavailable(f"openvino not installed: {exc}") from exc
        core = ov.Core()
        start = time.perf_counter()
        compiled = core.compile_model(str(self.path), "CPU",
                                      {"INFERENCE_NUM_THREADS": threads,
                                       "PERFORMANCE_HINT": "LATENCY"})
        self.load_seconds = time.perf_counter() - start
        self.compiled = compiled
        self.inputs = [i.get_any_name() for i in compiled.inputs]
        names = [o.get_any_name() for o in compiled.outputs]
        self.output = names.index("sentence_embedding") if "sentence_embedding" in names else 0
        self.details.update({"framework": "openvino", "inputs": self.inputs,
                             "output": names[self.output], "inference_threads": threads,
                             "ov_device": "CPU"})

    def _run(self, feed):
        result = self.compiled({k: feed[k] for k in self.inputs if k in feed})
        return result[self.compiled.outputs[self.output]]


def load_encoder(key: str, runtime: str, models_dir: Path, threads: int) -> Encoder:
    if key not in TEXT_MODELS:
        raise SystemExit(f"unknown text model {key!r}; known: {', '.join(TEXT_MODELS)}")
    model = TEXT_MODELS[key]
    if runtime == "st":
        return SentenceTransformerEncoder(model, models_dir, threads)
    if runtime.startswith("ort"):
        return OrtEncoder(model, models_dir, runtime, threads)
    if runtime.startswith("ov"):
        return OpenVinoEncoder(model, models_dir, runtime, threads)
    raise SystemExit(f"unknown runtime {runtime!r}; known: {', '.join(RUNTIMES)}")


def lock_entry(lock_path: Path, name: str) -> dict:
    return json.loads(lock_path.read_text())["models"].get(name, {})

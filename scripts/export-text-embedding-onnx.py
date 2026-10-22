#!/usr/bin/env python3
"""Export a NubArca text-embedding benchmark candidate to ONNX and prove parity.

Weights are downloaded by Hugging Face into its external cache and are never
committed. Each candidate is exported at the revision pinned in
RagTextEmbeddingModels (src/NubArca.Api/Ai/TextEmbeddings/RagTextEmbeddingModels.cs),
and the output layout matches that catalog exactly:

  <output>/qwen3-embedding-0.6b/model.onnx (+ model.onnx.data)   last_hidden_state [B,T,1024]
  <output>/qwen3-embedding-0.6b/tokenizer.json
  <output>/granite-embedding-311m-multilingual-r2/model.onnx      last_hidden_state [B,T,768]
  <output>/granite-embedding-311m-multilingual-r2/tokenizer.json
  <output>/granite-embedding-97m-multilingual-r2/model.onnx       last_hidden_state [B,T,384]
  <output>/granite-embedding-97m-multilingual-r2/tokenizer.json

The graph contract is exactly what OnnxTextEmbeddingProvider feeds:

  inputs   input_ids [B,T] int64, attention_mask [B,T] int64,
           position_ids [B,T] int64 (decoders only)
  output   last_hidden_state [B,T,hidden] float32
  NO past_key_values inputs: the provider refuses a graph that declares them.

Pooling, Matryoshka truncation and normalization are NOT in the graph; the
provider applies them from the catalog, and the parity check below applies them
the same way, so a graph that passes here is a graph the provider reads the
same way the reference does.

Parity: the exported graph, driven by the same tokenizer.json and the same
preprocessing the .NET provider applies (prefix, trailing EOS, truncation,
position ids, pooling, cut, L2), must reach cosine >= 0.999 against the
sentence-transformers reference at the same revision, for Italian and English
queries and passages. The script exits non-zero otherwise.

Run it in a controlled model-preparation environment (NOT inside the API
container) with: torch, transformers>=4.51, sentence-transformers>=3,
onnx, onnxruntime, tokenizers. Then copy the model directory under
Ai__Onnx__ModelDir and run `documents seed-profiles` and
`documents validate-model --profile <profile>`; see
docs/model-deployment/text-embeddings.md.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
from dataclasses import dataclass, replace
from pathlib import Path

import numpy as np


@dataclass(frozen=True)
class Candidate:
    checkpoint: str
    revision: str
    model_dir: str
    hidden: int
    # Stored dimensions to verify (Matryoshka cuts of `hidden`).
    dimensions: tuple[int, ...]
    pooling: str  # "last" | "cls" | "mean"
    query_prefix: str
    passage_prefix: str
    max_tokens: int
    eos_token: str | None
    decoder: bool
    # sentence-transformers prompt name for queries (None: no prompt).
    st_query_prompt_name: str | None


# Keep in lockstep with RagTextEmbeddingModels. A new revision, prefix or
# pooling is a new catalog entry and a new profile, never an edit here alone.
CANDIDATES: dict[str, Candidate] = {
    "qwen3-embedding-0.6b": Candidate(
        checkpoint="Qwen/Qwen3-Embedding-0.6B",
        revision="97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3",
        model_dir="qwen3-embedding-0.6b",
        hidden=1024,
        dimensions=(1024, 768),
        pooling="last",
        query_prefix=(
            "Instruct: Given a web search query, retrieve relevant passages "
            "that answer the query\nQuery:"
        ),
        passage_prefix="",
        max_tokens=1024,
        eos_token="<|endoftext|>",
        decoder=True,
        st_query_prompt_name="query",
    ),
    "granite-embedding-311m-multilingual-r2": Candidate(
        checkpoint="ibm-granite/granite-embedding-311m-multilingual-r2",
        revision="44399559930365213510b1ee2eb15ded83374f0e",
        model_dir="granite-embedding-311m-multilingual-r2",
        hidden=768,
        dimensions=(768,),
        pooling="cls",
        query_prefix="",
        passage_prefix="",
        max_tokens=1024,
        eos_token=None,
        decoder=False,
        st_query_prompt_name=None,
    ),
    "granite-embedding-97m-multilingual-r2": Candidate(
        checkpoint="ibm-granite/granite-embedding-97m-multilingual-r2",
        revision="835ad14087e140460703cf0fae09f97d469d65c2",
        model_dir="granite-embedding-97m-multilingual-r2",
        hidden=384,
        dimensions=(384,),
        pooling="cls",
        query_prefix="",
        passage_prefix="",
        max_tokens=1024,
        eos_token=None,
        decoder=False,
        st_query_prompt_name=None,
    ),
}

QUERIES = [
    "come rinnovo la carta d'identità?",
    "quanto ho pagato di bolletta della luce a marzo?",
    "how do I reset the router password?",
]
PASSAGES = [
    "Per il rinnovo della carta d'identità elettronica occorre prenotare un "
    "appuntamento presso l'ufficio anagrafe del Comune di residenza.",
    "Bolletta energia elettrica, periodo marzo: totale da pagare 84,20 euro, "
    "scadenza 15 aprile.",
    "To reset the administrator password, hold the reset button for ten "
    "seconds, then sign in with the default credentials printed on the label.",
]
# One passage of a few hundred tokens: longer than ModernBERT's 128-token local
# attention window, so the parity check exercises the sliding-window path and
# any sequence-length assumption the tracer baked into the graph.
PASSAGES.append(" ".join(PASSAGES) * 4)
PARITY_MIN_COSINE = 0.999


def export(c: Candidate, target: Path, opset: int, source: str, revision: str | None) -> None:
    import torch
    from transformers import AutoModel, AutoTokenizer

    target.mkdir(parents=True, exist_ok=True)

    # Eager attention: the SDPA / flash paths build masks with ops that have
    # broken ONNX exports for Qwen3 (see the deployment doc), and the graph's
    # numerics are what parity measures anyway.
    model = AutoModel.from_pretrained(
        source,
        revision=revision,
        torch_dtype=torch.float32,
        attn_implementation="eager",
    )
    model.eval()
    if c.decoder:
        model.config.use_cache = False

    tokenizer = AutoTokenizer.from_pretrained(source, revision=revision)
    # The provider truncates itself (keeping the closing token) and never pads
    # at batch size 1, so the persisted tokenizer must do neither.
    backend = tokenizer.backend_tokenizer
    backend.no_truncation()
    backend.no_padding()
    backend.save(str(target / "tokenizer.json"))

    class Wrapper(torch.nn.Module):
        def __init__(self, inner: torch.nn.Module) -> None:
            super().__init__()
            self.inner = inner

        def forward(self, input_ids, attention_mask, position_ids=None):
            kwargs = dict(input_ids=input_ids, attention_mask=attention_mask)
            if c.decoder:
                kwargs["position_ids"] = position_ids
                kwargs["use_cache"] = False
            return self.inner(**kwargs).last_hidden_state

    sample = tokenizer(["una frase di prova per l'esportazione"], return_tensors="pt")
    ids = sample["input_ids"]
    mask = sample["attention_mask"]
    args = (ids, mask)
    input_names = ["input_ids", "attention_mask"]
    axes = {
        "input_ids": {0: "batch", 1: "sequence"},
        "attention_mask": {0: "batch", 1: "sequence"},
        "last_hidden_state": {0: "batch", 1: "sequence"},
    }
    if c.decoder:
        args = (ids, mask, torch.arange(ids.shape[1], dtype=torch.long).unsqueeze(0))
        input_names.append("position_ids")
        axes["position_ids"] = {0: "batch", 1: "sequence"}

    with torch.inference_mode():
        torch.onnx.export(
            Wrapper(model).eval(),
            args,
            str(target / "model.onnx"),
            input_names=input_names,
            output_names=["last_hidden_state"],
            dynamic_axes=axes,
            opset_version=opset,
            do_constant_folding=True,
            # > 2 GB for Qwen3 in FP32: weights go to model.onnx.data next to
            # the graph, which ONNX Runtime resolves relative to model.onnx.
            external_data=True,
            dynamo=False,
        )


# ---- the .NET provider's preprocessing, restated -------------------------------


def provider_embed(session, tokenizer, c: Candidate, text: str, query: bool, dimension: int) -> np.ndarray:
    prepared = (c.query_prefix if query else c.passage_prefix) + text
    enc = tokenizer.encode(prepared, add_special_tokens=True)
    ids = [i for i, m in zip(enc.ids, enc.attention_mask) if m]
    if c.eos_token is not None:
        eos = tokenizer.encode(c.eos_token, add_special_tokens=False).ids
        if len(eos) != 1:
            raise SystemExit(f"{c.model_dir}: EOS token does not encode to one id")
        if not ids or ids[-1] != eos[0]:
            ids.append(eos[0])
    limit = max(2, c.max_tokens)
    if len(ids) > limit:
        ids = ids[: limit - 1] + [ids[-1]]

    names = {i.name for i in session.get_inputs()}
    if any(n.startswith("past_key_values") or n.startswith("past.") for n in names):
        raise SystemExit(f"{c.model_dir}: graph declares a KV cache; the provider refuses it")
    feed = {
        "input_ids": np.array([ids], dtype=np.int64),
        "attention_mask": np.ones((1, len(ids)), dtype=np.int64),
    }
    if "token_type_ids" in names:
        feed["token_type_ids"] = np.zeros((1, len(ids)), dtype=np.int64)
    if "position_ids" in names:
        feed["position_ids"] = np.arange(len(ids), dtype=np.int64)[None, :]

    hidden = session.run(["last_hidden_state"], feed)[0][0]
    if hidden.shape[-1] != c.hidden:
        raise SystemExit(f"{c.model_dir}: hidden size {hidden.shape[-1]} != {c.hidden}")
    if c.pooling == "last":
        vector = hidden[-1]
    elif c.pooling == "cls":
        vector = hidden[0]
    else:
        vector = hidden.mean(axis=0)
    vector = vector[:dimension].astype(np.float64)
    return vector / np.linalg.norm(vector)


def verify(c: Candidate, target: Path, source: str, revision: str | None) -> bool:
    import onnxruntime as ort
    from sentence_transformers import SentenceTransformer
    from tokenizers import Tokenizer

    session = ort.InferenceSession(str(target / "model.onnx"), providers=["CPUExecutionProvider"])
    tokenizer = Tokenizer.from_file(str(target / "tokenizer.json"))

    if c.eos_token is not None:
        probe = tokenizer.encode("ciao", add_special_tokens=True).ids
        eos = tokenizer.encode(c.eos_token, add_special_tokens=False).ids
        print(f"tokenizer_appends_eos={'yes' if probe and probe[-1:] == eos else 'no (provider appends)'}")

    ok = True
    for dimension in c.dimensions:
        reference = SentenceTransformer(
            source,
            revision=revision,
            device="cpu",
            truncate_dim=dimension if dimension != c.hidden else None,
            model_kwargs={"attn_implementation": "eager", "torch_dtype": "float32"},
        )
        reference.max_seq_length = c.max_tokens
        q_kwargs = {"prompt_name": c.st_query_prompt_name} if c.st_query_prompt_name else {}
        ref_q = reference.encode(QUERIES, normalize_embeddings=True, **q_kwargs)
        ref_p = reference.encode(PASSAGES, normalize_embeddings=True)

        worst = 1.0
        for texts, refs, query in ((QUERIES, ref_q, True), (PASSAGES, ref_p, False)):
            for text, ref in zip(texts, refs):
                ours = provider_embed(session, tokenizer, c, text, query, dimension)
                worst = min(worst, float(np.dot(ours, ref.astype(np.float64))))
        passed = worst >= PARITY_MIN_COSINE
        ok &= passed
        print(f"parity dimension={dimension} min_cosine={worst:.6f} {'ok' if passed else 'FAILED'}")
    return ok


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for block in iter(lambda: handle.read(1 << 20), b""):
            digest.update(block)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--model", required=True, choices=sorted(CANDIDATES))
    parser.add_argument("--output", type=Path, required=True, help="the future Ai__Onnx__ModelDir")
    parser.add_argument("--opset", type=int, default=17)
    parser.add_argument("--verify-only", action="store_true", help="skip export, re-run parity")
    parser.add_argument(
        "--source",
        help="a LOCAL checkpoint directory instead of the pinned Hub revision "
        "(dry runs of this script only; never for a production export)",
    )
    args = parser.parse_args()

    c = CANDIDATES[args.model]
    source, revision = (args.source, None) if args.source else (c.checkpoint, c.revision)
    if args.source:
        # A dry-run checkpoint (e.g. a tiny random model of the same
        # architecture) has its own width; keep the Matryoshka cut in scale.
        hidden = json.loads((Path(args.source) / "config.json").read_text())["hidden_size"]
        cuts = tuple(hidden * d // c.hidden for d in c.dimensions)
        c = replace(c, hidden=hidden, dimensions=cuts)
    target = args.output.resolve() / c.model_dir

    if not args.verify_only:
        export(c, target, args.opset, source, revision)
    ok = verify(c, target, source, revision)

    print(f"model={c.checkpoint} revision={revision or 'LOCAL (not pinned)'}")
    for path in sorted(target.iterdir()):
        if path.is_file():
            print(f"sha256 {sha256(path)}  {c.model_dir}/{path.name}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())

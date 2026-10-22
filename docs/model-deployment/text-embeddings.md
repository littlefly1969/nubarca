# Local text embeddings: default and benchmark candidates

> **Status: `multilingual-e5-small` remains the default and the only model any
> domain uses unless an operator says otherwise.** The other entries below are
> opt-in benchmark candidates. **Which of them, if any, replaces E5 is decided by
> a benchmark on the target server (Intel 12th gen, 32 GB, at most 20 GB for
> AI), and that has not run yet.** Nothing on this page is a target measurement.

The text-embedding catalog is code, not database:
`src/NubArca.Api/Ai/TextEmbeddings/RagTextEmbeddingModels.cs`. Each entry states
how its checkpoint must be fed (prefixes, pooling, dimensions, EOS, maximum
tokens). `rag seed-profiles` turns every entry into an `AiModel` and an
`AiProfile` with `IsDefault=false`. A profile does nothing until a domain names
it.

## Candidates

| Profile key | Catalog key / model dir | Hugging Face repo @ pinned revision | Licence | Stored dim | Pooling | Query / passage prefix |
|---|---|---|---|---|---|---|
| `rag-text-multilingual-e5-small-v1` (**default**) | `multilingual-e5-small` | `intfloat/multilingual-e5-small` | MIT | 384 | masked mean | `query: ` / `passage: ` |
| `rag-text-qwen3-embedding-0.6b-1024-v1` | `qwen3-embedding-0.6b-1024` / `qwen3-embedding-0.6b` | `Qwen/Qwen3-Embedding-0.6B` @ `97b0c614be4d77ee51c0cef4e5f07c00f9eb65b3` | Apache-2.0 | 1024 (native) | last token (`<|endoftext|>`) | `Instruct: Given a web search query, retrieve relevant passages that answer the query\nQuery:` / none |
| `rag-text-qwen3-embedding-0.6b-768-v1` | `qwen3-embedding-0.6b-768` / `qwen3-embedding-0.6b` | same weights | Apache-2.0 | 768 (Matryoshka cut of 1024) | last token | same |
| `rag-text-granite-embedding-311m-multilingual-r2-v1` | `granite-embedding-311m-multilingual-r2` | `ibm-granite/granite-embedding-311m-multilingual-r2` @ `44399559930365213510b1ee2eb15ded83374f0e` | Apache-2.0 (tokenizer: see below) | 768 | CLS | none / none |
| `rag-text-granite-embedding-97m-multilingual-r2-v1` | `granite-embedding-97m-multilingual-r2` | `ibm-granite/granite-embedding-97m-multilingual-r2` @ `835ad14087e140460703cf0fae09f97d469d65c2` | Apache-2.0 (tokenizer: see below) | 384 | CLS | none / none |

The candidates use `MaxTokens` 1024. All three models accept 32k tokens, and
the chunker's character budget fits well inside 1024. E5 stays at 512.

Sources (read 2026-10-21):

- Qwen3-Embedding-0.6B: <https://huggingface.co/Qwen/Qwen3-Embedding-0.6B>.
  - The pinned revision's `config_sentence_transformers.json` sets the query
    prompt above and an empty document prompt.
  - The card specifies last-token pooling, left padding, L2 normalization,
    Matryoshka from 32 to 1024 dimensions, 28 layers, and English instructions
    even for multilingual use.
  - Upstream commit `b22da495` ("Automatically add EOS via Tokenizer") makes
    `tokenizer.json` append `<|endoftext|>` (id 151643). `<|im_end|>` is the
    chat EOS and is **not** the pooled token.
- Granite R2 311M: <https://huggingface.co/ibm-granite/granite-embedding-311m-multilingual-r2>.
  - ModernBERT, 22 layers, hidden size 768.
  - `1_Pooling/config.json` sets `pooling_mode_cls_token: true`, followed by a
    `Normalize` module.
  - The query and document prompts are empty.
  - Matryoshka dimensions are 768, 512, 384, 256 and 128. Only 768 is
    catalogued; a cut is a new entry.
  - The repo ships `onnx/model.onnx` and OpenVINO IR (FP32 and INT8).
- Granite R2 97M: <https://huggingface.co/ibm-granite/granite-embedding-97m-multilingual-r2>.
  - ModernBERT, 12 layers, hidden size 384, 180k vocabulary.
  - CLS pooling, normalized, no prompts, no Matryoshka.
  - The repo ships ONNX and OpenVINO.
- multilingual-e5-small: <https://huggingface.co/intfloat/multilingual-e5-small>
  (MIT).
- Licence and benchmark summary for all candidates: the model availability
  report behind Document Intelligence v1. All benchmark numbers there are
  developer-claimed.

**Licence note: Granite tokenizer.** The 311M card states that its 262k
tokenizer is derived from Gemma 3's, and that use of the Gemma tokenizer is
subject to the Gemma Terms of Use. The 97M tokenizer is a reduced version of
the 311M one, and its card does not repeat the note. Have this reviewed before
shipping either Granite model commercially. The weights are Apache-2.0.

Excluded, and why:

- Jina v5 nano/small: CC BY-NC 4.0, not commercial.
- embeddinggemma: Gemma Terms plus AUP.
- bge-m3: not shortlisted this round.

## What the provider does for them

`OnnxTextEmbeddingProvider` handles all of this. Each behaviour has tests in
`tests/NubArca.Api.Tests/Rag/TextEmbeddingCandidateModelTests.cs`.

- **Last-token pooling** reads the last position whose `attention_mask` is 1.
  - Under left padding that is position T-1. Under right padding it is the
    position before the first pad. The provider does not need to know which
    side was padded.
  - Inference is batch size 1 with no padding, so today it is always the final
    token.
- **Trailing EOS.** For an entry with `EosToken`, the provider resolves the
  token through `tokenizer.json`.
  - It must encode to exactly one id. Otherwise the model is reported as
    `text_embedding_model_unavailable`: the wrong tokenizer is an install
    problem, not a content failure.
  - If the encoded sequence does not already end with that id, the provider
    appends it.
  - Truncation keeps the first `MaxTokens-1` ids plus the final id, so the
    pooled token is always EOS.
- **Matryoshka.** `NativeDimension` is the graph's hidden size, and `Dimension`
  is the stored size.
  - The pooled vector is cut to the first `Dimension` components **and then**
    L2-normalized. Cutting an already-normalized vector would leave it shorter
    than 1.
  - The graph is checked against the native size. A 768 profile is ready only
    at dimension 768.
- **`position_ids`** is fed when the graph declares it, as `cumsum(mask)-1`
  with pads set to 1. Without padding this is `0..T-1`. `token_type_ids` keeps
  its existing rule: it is fed only when declared.
- **KV-cache graphs are refused**, not fed. A graph that declares
  `past_key_values.*` (or `past.*`) inputs is reported as
  `text_embedding_model_unavailable`.
  - With OpenVINO this happens at readiness, because the session is compiled
    there anyway. On the CPU provider it happens before the first inference.
  - Why refuse rather than feed: an empty past needs the cache's layer count,
    head count, head size and dtype, which is a second description of the
    model that can drift from the graph. An embedding is one forward pass and
    never needs a cache, so exporting without one is the simpler contract.
- **Per-profile evidence floor.** `RagRetriever` uses
  `RagTextEmbeddingModels.MinimumVectorScoreFor(profileKey, Rag:MinimumVectorScore)`
  (see below).

Not done: **batching.** `ITextEmbeddingProvider` embeds one text per call, and
the indexer (owned by the ingestion slice) calls it per chunk. Passage batching
needs a contract change and a measured batch size per model on the target CPU.
It is a follow-up, to be done once the benchmark shows whether throughput is the
bottleneck. The pooling and position-id code already honours padding, so a
batched call can reuse it.

## The evidence gate is per profile, and the candidates are NOT calibrated

`Rag:MinimumVectorScore` (default 0.80) was set for E5's cosine range. A purely
semantic hit at or above it counts as strong evidence on its own. Cosine is not
calibrated across checkpoints, so:

- E5 has no own value and keeps reading `Rag:MinimumVectorScore`, so an
  operator override still applies.
- Every candidate carries `MinimumVectorScore = 1.0`
  (`UncalibratedMinimumVectorScore`). **This is a placeholder, not a
  calibration.**
  - A cosine of 1.0 is reached only by an identical vector. Under a candidate
    profile, semantic hits still rank through RRF fusion, but cannot open the
    gate alone. Evidence stays on the lexical gate, which has a golden set
    behind it.
  - This is safe but wasteful. **To calibrate:** run the golden set under the
    candidate profile, pick the floor that admits true semantic-only matches
    without admitting the "nothing relevant" questions, and write it into the
    catalog entry in a reviewed change.

## File layout

Model directories live under `Ai__Onnx__ModelDir` (production mounts it
read-only at `/models/ai`):

```text
<Ai__Onnx__ModelDir>/
  multilingual-e5-small/                   model.onnx, tokenizer.json
  qwen3-embedding-0.6b/                    model.onnx, model.onnx.data, tokenizer.json
  granite-embedding-311m-multilingual-r2/  model.onnx, tokenizer.json
  granite-embedding-97m-multilingual-r2/   model.onnx, tokenizer.json
```

- Both Qwen3 profiles read the same directory. The ONNX session cache is keyed
  by model path, so the weights are resident once even if both profiles are
  used.
- Qwen3 in FP32 exceeds protobuf's 2 GB limit, so its weights sit in
  `model.onnx.data` next to the graph. ONNX Runtime resolves that file relative
  to `model.onnx`, so keep the two files together.

## Export and install

Run `scripts/export-text-embedding-onnx.py` in a model-preparation environment.
That means the target server or a machine like it, **not** inside the API
container and **not** in CI. It needs:

- `torch`
- `transformers>=4.51`
- `sentence-transformers>=3`
- `onnx`
- `onnxruntime`
- `tokenizers`

```bash
python3 scripts/export-text-embedding-onnx.py --model qwen3-embedding-0.6b --output /path/to/model-staging
python3 scripts/export-text-embedding-onnx.py --model granite-embedding-311m-multilingual-r2 --output /path/to/model-staging
python3 scripts/export-text-embedding-onnx.py --model granite-embedding-97m-multilingual-r2 --output /path/to/model-staging
```

For each candidate, the script:

1. Downloads the checkpoint **at the pinned revision**.
2. Exports `input_ids`, `attention_mask` and (for Qwen3) `position_ids` to
   `last_hidden_state`. It uses eager attention, `use_cache=False`, no KV
   inputs, opset 17 and external data.
3. Saves `tokenizer.json` with truncation and padding disabled, because the
   provider truncates itself.
4. **Verifies parity** by re-implementing the provider's preprocessing in
   Python, from the exported `tokenizer.json` and ONNX Runtime:
   - prefix, trailing EOS, truncation, position ids, pooling, cut, L2;
   - against the `sentence-transformers` reference at the same revision;
   - on Italian and English queries and passages, at every catalogued dimension;
   - requiring cosine ≥ 0.999.

   It exits non-zero on failure. It also reports whether the tokenizer appends
   EOS itself.
5. Prints the SHA-256 of every produced file. Record those hashes next to the
   installation's model inventory.

The script itself has been dry-run, but **not against real weights**. The run
used `--source` on tiny, randomly initialised Qwen3 and ModernBERT checkpoints
with the same architectures and the same tokenizer post-processors. Versions:
torch 2.14.1, transformers 5.20.2, sentence-transformers 6.2.0, onnxruntime
1.31.0. That run checked three things:

- The export produced the declared inputs and no KV inputs.
- Parity held at cosine 1.000000, including a passage longer than ModernBERT's
  local attention window.
- The Matryoshka cut path worked.

Only the real export on the target server proves anything about the real
checkpoints.

Then, on the server:

1. Copy the model directory under `Ai__Onnx__ModelDir`, following
   `deploy/FAST_DEPLOY.md` for anything touching production.
2. Seed and validate:

   ```bash
   $DC exec api dotnet NubArca.Api.dll rag seed-profiles
   $DC exec api dotnet NubArca.Api.dll rag validate-model --profile rag-text-qwen3-embedding-0.6b-768-v1
   ```

`rag validate-model` also checks for a system pgvector table of the profile's
dimension, and only a 384-d table exists. For 768-d and 1024-d profiles it
prints `vector_table=FAILED`. That is expected for the owner-private
`user-documents` domain, which uses exact in-process cosine and no pgvector
table. It does mean that **system domains (`product-help`) must stay on a 384-d
profile** (E5 or Granite 97M) until a migration adds a table for another
dimension.

The Granite repos also ship their own `onnx/model.onnx`, and they ship OpenVINO
IR. Prefer the script's export: its graph contract and parity are verified
against what the provider feeds. A vendor file is acceptable only if
`--verify-only` against it passes. To check one, place it as
`<staging>/<model dir>/model.onnx` next to an exported `tokenizer.json`.

## Switching a domain's profile (opt-in)

Owner-private documents never inherit an installation-wide profile. The domain
must state both settings:

```text
Rag__Domains__user-documents__SemanticEnabled=true
Rag__Domains__user-documents__TextEmbeddingProfileKey=rag-text-qwen3-embedding-0.6b-768-v1
```

- `docker-compose.prod.yml` forwards these two keys to api and worker. In the
  production `.env`, write the variable with `user_documents`, because a
  hyphen cannot appear in a Compose interpolation name:
  `Rag__Domains__user_documents__TextEmbeddingProfileKey=…`. Other `Rag__*`
  keys are still not forwarded. See
  [`../document-intelligence/deployment-runbook.md`](../document-intelligence/deployment-runbook.md).
- A switch makes semantic retrieval unavailable for chunks not yet embedded
  under the new profile. Re-embed each owner with
  `documents index --owner <id> --embed`.
- Vectors are keyed by profile, so the old profile's vectors stay and switching
  back needs no re-embed. That also makes side-by-side A/B possible.

## Memory and runtime notes

These are estimates from parameter counts, not measurements:

| Model | Params | FP32 weights resident |
|---|---|---|
| multilingual-e5-small | ~118M | ~0.5 GB |
| granite-embedding-97m-multilingual-r2 | ~97M | ~0.4 GB |
| granite-embedding-311m-multilingual-r2 | ~311M | ~1.25 GB |
| Qwen3-Embedding-0.6B | ~0.6B | **~2.4 GB** |

- Sessions are cached for the process lifetime with no eviction, in the **API**
  process. The API embeds every query, next to the SigLIP2 and face sessions.
  The same applies to whichever process runs indexing.
- Peak working memory adds activations of roughly `T × hidden × layers`. At
  T ≤ 1024 and batch 1 this is small compared with the weights.
- Production uses OpenVINO through `OnnxInferenceSessionFactory`, which **forces
  FP32 inference precision** on the OpenVINO EP. That is deliberate for photo
  parity and is not changed here. All text models share one device setting,
  `Ai:Onnx:OpenVino:RagTextDevice`, which defaults to CPU.
- **INT8 is a follow-up measurement, not a decision.**
  `OpenVINO/Qwen3-Embedding-0.6B-int8-ov` exists, and the Granite repos ship
  INT8 OpenVINO IR. This provider loads ONNX through ONNX Runtime, so using an
  OpenVINO IR needs a different loader. A quantized ONNX (for example dynamic
  INT8 from `onnxruntime.quantization`) would load as is, but must pass the
  same parity check against a relaxed, *measured* threshold before any
  catalog entry points at it. Measure quality on the golden set and
  throughput on the target CPU before considering either.

## What the target-server benchmark must produce

For each candidate, measured on the target hardware with the production
runtime (OpenVINO EP, FP32):

- Passages per second at batch size 1 on representative chunks.
- Query latency, p50 and p95.
- Resident memory of the API process with the model warm.
- Retrieval quality on the private and Product Help golden sets (Recall@5,
  MRR), lexical-only versus hybrid.
- The calibrated `MinimumVectorScore` for the chosen profile.

Only then should a domain's `TextEmbeddingProfileKey` change.

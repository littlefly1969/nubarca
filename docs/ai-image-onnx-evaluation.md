# AI image embeddings — local ONNX evaluation (Phase 2A)

This is the read-only ONNX image evaluation harness. The model decision is now
complete: the catalog contains only the production multimodal SigLIP2 So400m
profile. For asset preparation, text retrieval, the 1152 pgvector migration and
rollout use [multimodal-photo-search.md](multimodal-photo-search.md).

External providers, face/document/tag pipelines, and UI are explicitly out of
scope here.

> **Profile lifecycle:** how the active photo-similarity profile is *selected*
> and *switched* (the `Ai__PhotoSimilarityProfileKey` config key, profile-keyed
> coverage/backfill, and the safe switch workflow) lives in
> [docs/ai-photo-profile-lifecycle.md](ai-photo-profile-lifecycle.md). This doc
> stays focused on the read-only evaluation harness. The eval profiles seeded
> here (`photo-siglip2-so400m-patch14-384-v2`) remains
> **non-default** until explicitly selected via that config key.

## Production model

| Catalog key (`AiModel.Key` / dir) | Eval profile key | Input | Resize | Norm | Dim | Notes |
|---|---|---|---|---|---|---|
| `siglip2-so400m-patch14-384` | `photo-siglip2-so400m-patch14-384-v2` | 384² | stretch→square | mean/std 0.5 | 1152 | Image and text towers from one checkpoint; production profile. |

Dimensions/preprocessing above are the harness's **documented assumptions** —
verify them against your actual export with `ai onnx image embed-test` (the
reported `dim` must match) before trusting `compare` output. The config lives in
code (`OnnxImageModels.Catalog`); an `AiProfile` links to it via its short
`ConfigHash` (= the catalog key). Edit the catalog if an export differs (e.g.
different input/output tensor names).

### Intended use
- Whole-image **visual similarity** (photo "more like this", dedup-ish grouping).
- One embedding vector per image, L2-normalized, compared by cosine.

## Preprocessing (exact assumptions)

Implemented in `OnnxImagePreprocessor` (ImageSharp, deterministic):

1. Decode bytes as **RGB24** (alpha dropped).
2. **EXIF orientation applied** (`AutoOrient`) so rotated/portrait images embed as viewed.
3. Resize to the model's square input with a fixed **Bicubic** resampler:
   - `stretch` (SigLIP): resize directly to `InputSize × InputSize`.
   - `shortest-crop` (DINOv2/ImageNet): resize shortest side to `InputSize`, center-crop.
4. Emit an NCHW (`1×3×H×W`) float tensor, channel-major, value `(pixel/255 − mean[c]) / std[c]`.

Properties: one image at a time (bounded memory); original blobs are **never**
modified; **no** thumbnail/derived artifact is produced as a side effect;
corrupt/unsupported images are caught and counted as failures (never a crash).

## Output normalization
Raw model output → `OnnxImageEmbeddings.Finalize`: **dimension-validated**
(must equal the profile dimension — a mismatch is a surfaced failure, never a
silent reshape), **NaN/Infinity rejected**, then **L2-normalized**. With unit
vectors, cosine == dot product.

## ONNX acquisition / export path

Weights are **never committed**. Use the pinned, paired export script described
in [multimodal-photo-search.md](multimodal-photo-search.md). It emits an
image-only graph (`pixel_values → image_embeds`) and a text-only graph
(`input_ids + attention_mask → text_embeds`) from the same checkpoint revision.
For this FixRes checkpoint the mask input is retained for graph compatibility
but every one of the 64 positions is `1`, matching the official no-mask
`AutoProcessor` call. A tokenizer padding mask (`0` on pads) produces valid
1152-dimensional vectors in the wrong cross-modal space. Do not deploy a
combined graph and do not independently source the two towers.

## Model directory convention

```
Ai__Onnx__ModelDir=/models/ai
/models/ai/siglip2-so400m-patch14-384/model.onnx
/models/ai/siglip2-so400m-patch14-384/text_model.onnx
/models/ai/siglip2-so400m-patch14-384/tokenizer.json
```

`/models/` and `*.onnx` (etc.) are git-ignored. On the prod/lab host, mount the
model dir read-only into the api+worker containers and set `Ai__Onnx__ModelDir`
to the in-container path.

## License notes (verify before production use)
- **SigLIP2** (`google/siglip2-*`): Apache-2.0 (confirm on the model card at export time).
Record the exact license/version you export; do not redistribute weights via this repo.

## CPU/RAM expectations
CPU-only runtime (`Microsoft.ML.OnnxRuntime`). Quality > speed: background
embedding may take **hundreds of ms to a few seconds per image** on CPU
and must be measured on the deployment CPU. RAM includes one resident image
session in the worker and a lazily loaded text session in the API.
Sessions are loaded once and reused; concurrency is bounded by
`Ai__MaxConcurrency` and each inference by `Ai__TimeoutSeconds`.

## Risks
- **Export mismatch:** wrong input/output tensor name or unpooled output → dimension-validation failure (caught, surfaced — fix the catalog/export).
- **Tower mismatch:** image and text graphs from different revisions silently destroy cross-modal quality; the export revision is pinned.
- **Preprocessing drift:** wrong normalization/resize silently degrades quality; `compare` is the sanity check.
- **CPU latency** on a large corpus — Phase 2B backfill must stay cooperative/sliceable.
- **Dimension change** requires a new profile + full reindex; dimensions/spaces are never mixed.

## Validation criteria
1. **Retrieval quality** — `ai onnx image compare` top-k looks coherent on real photos (near-dupes ~1.0; semantically related high; unrelated low).
2. **Throughput** — `ai onnx image benchmark` avg + p95 ms/image acceptable for a background reindex of the corpus.
3. **Footprint** — RAM/session fits the host.
4. **Cross-modal quality** — evaluate natural Italian text queries separately from image similarity.
5. **Licensing** — confirmed permissive.

## CLI

```
ai onnx image models                                    # candidates + presence on disk
ai onnx image seed-profiles                             # seed eval profiles (NOT default; inert w/o weights)
ai onnx image benchmark   --profile <key> [--limit N]   # dry-run timings (avg, p50/p95, dim) — no writes
ai onnx image embed-test  --profile <key> --file <id>   # one image: dim, L2 norm, ms
ai onnx image compare     --profile <key> --file <id> [--limit N] [--candidate-limit M]
                                                        # owner-private top-k by this model — no writes
```
All output is sanitized: counts/timings/dimensions + file **names** + rounded
scores only — never raw vectors, `BlobObjectId`, SHA, `StorageKey`, or paths.
The harness requires AI enabled (`Ai__Enabled=true`) + an ONNX eval profile
seeded + the model file present; otherwise commands report `unavailable` cleanly.

## pgvector

The current production design is documented in
[multimodal-photo-search.md](multimodal-photo-search.md). Summary:
- DB image switched to **`pgvector/pgvector:pg17`** (`CREATE EXTENSION vector`,
  via a fault-tolerant additive migration that skips cleanly without pgvector).
- A **1152-dim** vector table `blob_embedding_vectors_1152` alongside (not
  replacing) `EmbeddingBytes`, with a **cosine HNSW** index
  (`vector_cosine_ops`, `<=>`, `m=16`, `ef_construction=96`).
- `PhotoSimilarityService` prefers ANN when the active profile is vector-indexed
  and **falls back to exact-scan** otherwise (the `Take(50_000)` cap remains for
  the fallback only).
- `ai photos embeddings vector-sync --profile <key>` populates vectors from the
  canonical embeddings (idempotent, profile-keyed, dimension/finite-validated).

The legacy 768 table is inert during reindex and removed by the guarded
`retire-legacy-768 --execute` command after complete 1152 coverage.

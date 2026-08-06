# Multimodal photo search — SigLIP2 So400m / 1152

NubArca uses one production photo-embedding profile for two deliberately
separate retrieval modes:

- **image → image**: the existing Similar Photos surfaces;
- **text → image**: `GET /api/images/semantic`, ordered by cross-modal relevance.

There is no combined image+text query and no score/rank fusion. Both towers come
from the exact same `google/siglip2-so400m-patch14-384` checkpoint and emit
L2-normalized 1152-dimensional vectors in one shared cosine space.

The export script pins Hugging Face revision
`c65677ac77ca25276518923f7c58cbf5d81ea602`; changing it requires a new reviewed
profile version and a complete reindex.

## Quality invariants

The following are correctness requirements, not tuning options:

1. Image and text ONNX exports MUST come from the same checkpoint revision.
2. The image graph output is `image_embeds`; the text graph output is
   `text_embeds`. Do not substitute an arbitrary hidden state.
3. Image preprocessing is RGB, 384×384 stretch, rescale 1/255, mean/std 0.5.
4. The pinned checkpoint tokenizer is used verbatim with EOS, right padding
   and truncation to exactly 64 tokens.
5. The FixRes text tower attends all 64 fixed-padding positions. The ONNX graph
   retains its `attention_mask` input, but runtime MUST send all ones; masking
   pad positions moves text embeddings out of the image-aligned space.
6. Runtime validates dimension, finiteness and fixed token count, then L2
   normalizes both towers. It fails closed on incompatible assets.
7. Do not quantize the first production baseline. Benchmark a quantized export
   against the FP32 baseline before considering it.

Model binaries and tokenizer data are deployment assets and are never committed.

## Asset preparation

In a disposable model-preparation environment:

```bash
python -m venv .venv-models
. .venv-models/bin/activate
pip install torch 'transformers>=4.57,<5' onnx onnxruntime onnxscript safetensors
python scripts/export-siglip2-so400m-onnx.py --output /srv/ai-models
```

Expected layout:

```text
/srv/ai-models/siglip2-so400m-patch14-384/
├── model.onnx                 # pixel_values -> image_embeds[batch,1152]
├── model.onnx.data            # when ONNX external data is required
├── text_model.onnx            # input_ids+attention_mask -> text_embeds[batch,1152]
├── text_model.onnx.data       # when ONNX external data is required
└── tokenizer.json             # fixed 64-token policy embedded
```

Mount the directory read-only at the existing `Ai__Onnx__ModelDir` in both API
and worker containers. `model.onnx` is used by the worker backfill;
`text_model.onnx` and `tokenizer.json` are used lazily by the API.

## Database and profile

Migration `AddPhotoMultimodalVectorIndex1152` creates:

```text
blob_embedding_vectors_1152.embedding vector(1152)
ix_bev1152_embedding_hnsw_cosine (m=16, ef_construction=96)
```

The only real ONNX photo profile seeded by the image CLI is:

```text
photo-siglip2-so400m-patch14-384-v2
```

The old 768-dimensional table is deliberately not used by application code.
It remains inert during the reindex and is removed by the guarded retirement
command only after the new canonical and vector coverage both reach 100%.

## Rollout

Use the standard production compose wrapper/files documented in `CLAUDE.md`.
Back up first, then:

```bash
$DC build api worker frontend
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll db migrate
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll ai onnx image seed-profiles
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll ai onnx image models
```

Validate one real image before writing anything:

```bash
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll ai onnx image embed-test \
  --profile photo-siglip2-so400m-patch14-384-v2 --file <owner-file-guid>
```

Run the profile-keyed backfill while the legacy data is still inertly present:

```bash
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll jobs enqueue \
  ai-photos-embeddings-backfill \
  --profile photo-siglip2-so400m-patch14-384-v2
```

Monitor until both missing counts are zero:

```bash
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll ai photos embeddings coverage \
  --profile photo-siglip2-so400m-patch14-384-v2
```

Set and restart API+worker:

```env
Ai__PhotoSimilarityProfileKey=photo-siglip2-so400m-patch14-384-v2
```

Exercise both independent paths:

```text
GET /api/files/{fileId}/similar
GET /api/images/semantic?q=cane%20nero%20sulla%20neve
```

## Quality gate

Before retiring 768, evaluate a fixed owner-private corpus with:

- image→image precision@10 for near duplicates, same event and same subject;
- text→image Recall@10, nDCG@10 and human relevance judgments;
- at least 50 natural Italian queries spanning objects, actions, scenes,
  colours, weather and time of day;
- p50/p95 image-backfill time and text-query latency;
- privacy checks for another owner, Private Vault and media-library exclusions.

The semantic endpoint returns relevance order and intentionally hides raw scores,
vectors, profile ids, blob ids and storage information.
Before ranking, text-to-image retrieval excludes candidates whose known width or
height is below 128 pixels. This removes camera `.THM` sidecars, icons and tiny
thumbnails that otherwise behave as semantic hubs. Missing dimensions remain
eligible. The rule is applied consistently to pgvector, the canonical-vector
fallback and physical-filter-first natural-gallery searches; it does not alter
the normal gallery, image-to-image similarity, embeddings or source blobs.
It is authenticated and rate-limited per client IP (default 30 requests/60s;
`RateLimits__SemanticSearch__PermitLimit` and `WindowSeconds`). ONNX inference is
also bounded by `Ai__MaxConcurrency` and should set a conservative
`Ai__Onnx__IntraOpThreads` on CPU deployments.

## Retire the legacy 768 profile

Dry-run first:

```bash
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll ai photos embeddings retire-legacy-768
```

The command refuses to become ready unless the configured active profile is the
approved 1152 v2 profile and both canonical + pgvector coverage are complete.
Then execute:

```bash
$DC run --rm -e Jobs__WorkerEnabled=false api dotnet NubArca.Api.dll \
  ai photos embeddings retire-legacy-768 --execute
```

This disables 768-dimensional image profiles/models, deletes their canonical
photo embeddings and drops `blob_embedding_vectors_768`. It never touches face
or document embeddings. The operation is intentionally explicit and destructive.

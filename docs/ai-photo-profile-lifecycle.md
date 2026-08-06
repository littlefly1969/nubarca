# AI photo embedding profile lifecycle

> The lifecycle rules remain valid. The selected production profile is now
> `photo-siglip2-so400m-patch14-384-v2` (1152); see
> [multimodal-photo-search.md](multimodal-photo-search.md). Historical examples
> below retain the former 768 profile to document the previous rollout.

How NubArca chooses and switches the **active photo-similarity embedding
profile** safely. The goal of this layer is to make future model changes
**explicit, reversible, and profile-keyed** — never an implicit "use the latest
installed model". This slice adds the lifecycle controls only; it does **not**
benchmark more models, promote ONNX to the production default, run a real-model
mass backfill, add pgvector, or add UI.

Stable invariants live in [CLAUDE.md](../CLAUDE.md); the ONNX evaluation harness
is in [docs/ai-image-onnx-evaluation.md](ai-image-onnx-evaluation.md).

## The one knob: `Ai__PhotoSimilarityProfileKey`

A single config value (`AiOptions.PhotoSimilarityProfileKey`, env
`Ai__PhotoSimilarityProfileKey`, a stable `AiProfile.Key` — never a GUID) names
the active photo profile. It is the explicit selector for **both**:

- **reads** — `PhotoSimilarityService` (the `/api/files/{id}/similar` endpoint and
  `ai photos similar`) searches **only** this profile's embeddings, and
- **writes** — `ai.photos.embeddings.backfill` with **no** `--profile` writes
  **this** profile (so the default backfill always targets what similarity reads).

### Resolution precedence (fully explicit)

1. **operator override** — CLI `--profile <key>` (similarity / backfill), or
2. **configured** — `Ai__PhotoSimilarityProfileKey`, or
3. **documented fallback** — the capability's *default* profile (the partial-unique
   one-default-per-capability `AiProfile`, i.e. the deterministic image profile
   `det-image-embedding-v1` in the current lab).

There is **no** "latest installed model" heuristic, and a single search/backfill
**never mixes** embeddings from more than one profile (every query/candidate row
is filtered by the resolved `ProfileId`).

### Read vs. write readiness (important)

- **Read** (similarity) validates the profile is *usable for comparison*:
  enabled, capability `image-embedding`, a positive `Dimension`, and an enabled
  model. It deliberately does **not** require backend/model-file readiness —
  reading already-stored owner-private embeddings never needs the live model. So
  you can switch the active profile to an ONNX profile and immediately serve
  similarity from its **already-backfilled** vectors even if the weights aren't
  mounted on that host.
- **Write** (backfill) goes through the backend resolver, which **does** require
  readiness (e.g. the ONNX model file under `Ai__Onnx__ModelDir`). A
  missing/unavailable model is a clean **no-op** (at most one aggregate transient
  diagnostic, never per-blob `skipped`/`failed` rows).

> **pgvector (Phase 2B foundation).** The read path now **prefers a pgvector HNSW
> ANN query** when the active profile is vector-indexed, and **falls back to
> exact-scan** otherwise — same owner-private result, profiles never mixed.
> Indexing a profile's embeddings into pgvector is a separate, idempotent step
> (`ai photos embeddings vector-sync --profile <key>`). Reading still needs no
> live model. See [docs/ai-photo-pgvector.md](ai-photo-pgvector.md).

If the resolved profile is missing / inactive / wrong capability / wrong
dimension / has no model, the **API** returns a clean empty result
(`profileAvailable=false`) and the **operator CLI** prints a sanitized reason
(`profile-not-found`, `profile-disabled`, `capability-mismatch`,
`profile-dimension-invalid`, `model-unavailable`, `no-default-profile`).

## Current baseline

- **Deterministic** profile `det-image-embedding-v1` (dim 32, cosine) — the
  current production/lab default and fallback; **48,760** embeddings indexed.
  Dev/test only; **not** semantically meaningful, but the active profile today.
- **ONNX evaluation** profile `photo-siglip2-base-patch16-384-v1`
  (provider `onnx`, **dim 768**, cosine) — the validated SigLIP2-base baseline.
  Seeded **non-default**; usable only when explicitly selected and (for writes)
  when its weights are present. Other eval profiles
  (`photo-siglip2-so400m-patch14-384-v1` dim 1152, `photo-dinov2-base-v1` dim 768)
  remain non-default too.

We are **not** benchmarking more models in this slice because the **hardware is
being changed**; re-run the harness on the new hardware before choosing the
Phase 2B model. **pgvector now has a foundation** (768-dim table + HNSW; see
[docs/ai-photo-pgvector.md](ai-photo-pgvector.md)). Because the ANN index is
built for one fixed dimension, only **768** has a table today; a different chosen
dimension (e.g. 1152) just needs its own additive dimension-specific table before
its profile can be vector-indexed — no redesign, and exact-scan covers it
meanwhile.

## Operator workflows (CLI)

All output is aggregate/owner-private and sanitized — counts, stable keys,
dimensions, metrics, sanitized reason tokens, file names, rounded scores only.
Never raw vectors, `BlobObjectId`, SHA, `StorageKey`, or physical paths.

### Inspect the active profile

```bash
ai photos embeddings active-profile
# config_key=<key|(unset)> source=config|default-fallback usable=<bool>
# profile=<key> capability=image-embedding dimension=<dim> distance_metric=<metric> reason=<token|->
```

### Check coverage before switching

```bash
ai photos embeddings coverage --profile <profile-key>
# profile=<key>
# eligible_images=<count>      # image blobs referenced by an active FileItem
# embedded=<count>             # of those, indexed for <profile-key>
# missing=<count>              # == the backfill's pending count for <profile-key>
# coverage_percent=<percent>
# dimension=<dim>
# distance_metric=<metric>
```

Eligibility is **identical** to the backfill (image-category blob referenced by a
non-deleted `FileItem`), so `missing` is exactly what a backfill would still
process.

### Backfill a specific profile (profile-keyed, idempotent)

```bash
# Seed eval profiles once (non-default; inert without weights):
ai onnx image seed-profiles

# Index a specific profile explicitly (recommended during a migration):
jobs enqueue ai-photos-embeddings-backfill --profile <profile-key>

# With no --profile it uses Ai__PhotoSimilarityProfileKey (else the default):
jobs enqueue ai-photos-embeddings-backfill
```

The backfill writes **only** the requested profile (`BlobEmbedding` is unique per
`(BlobObjectId, ProfileId)`), is **idempotent** (already-indexed blobs drop out of
the candidate query), and **never overwrites** another profile's embeddings.

### Compare / query similarity for a profile

```bash
# Active profile (or default fallback):
ai photos similar --file <file-id> --limit 10
# Operator override of the active profile (read path; no live model needed):
ai photos similar --file <file-id> --profile <profile-key> --limit 10
# ONNX harness on-the-fly compare (re-embeds with the live model; needs weights):
ai onnx image compare --profile <profile-key> --file <file-id> --limit 10
```

## Switching the active profile safely

Profile switching is a **config change + restart** (the project convention; no
schema change, no destructive command). Readiness checklist before switching:

1. The target profile **exists** and is **active** — `ai profiles`.
2. Capability is **image-embedding** and dimension/metric are valid —
   `ai photos embeddings coverage --profile <key>`.
3. Coverage is acceptable (ideally `missing=0`) — same command. Backfill it first
   if not: `jobs enqueue ai-photos-embeddings-backfill --profile <key>`.
4. For writes, model/provider readiness is OK if you intend to keep indexing —
   `ai onnx image models` (`model_present=True`) / `ai status`.

Then set the key and restart (prod uses both compose files + the env file):

```env
# .env
Ai__PhotoSimilarityProfileKey=photo-siglip2-base-patch16-384-v1
```

```bash
cd "$NUBARCA_PRODUCTION_CHECKOUT"
DC="docker compose -f docker-compose.prod.yml -f docker-compose.prod.local.yml --env-file .env"
$DC up -d api frontend
$DC --profile worker up -d worker
ai photos embeddings active-profile   # confirm source=config, usable=True
```

**Reversible:** unset the key (or point it back) and restart to return to the
previous profile — the old profile's embeddings are untouched and coexist. Do
**not** auto-switch after seeding or benchmarking.

## Old-profile cleanup (planned — not implemented this slice)

Pruning a superseded profile's embeddings is **documented only** here; no
destructive command ships in this slice (we prefer docs/tests over destructive
behavior). When implemented later it MUST: default to `--dry-run` (or require an
explicit confirm flag), report **aggregate counts only**, delete **only**
`BlobEmbedding` rows for the selected profile, and **never** touch physical files
or `BlobObject` rows.

```bash
# Future, not yet implemented:
ai photos embeddings prune --profile <old-profile> --dry-run   # counts only
ai photos embeddings prune --profile <old-profile>             # requires confirm
```

Until then, a superseded profile can simply be left in place (coexisting, unread
once the active key points elsewhere) or its rows removed by a reviewed
maintenance script.

## Guarantees / non-leak

- No raw vectors, `BlobObjectId`, SHA, `StorageKey`, or physical paths in any
  API/CLI output added here.
- Owner-private only; no public-share behavior change; no cross-owner search.
- No schema change and no migration (existing `AiProfile`/`AiModel` fields +
  config only).

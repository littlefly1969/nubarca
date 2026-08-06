# Photo similarity — pgvector foundation (Phase 2B)

> Historical 768-dimension foundation. The active replacement is the 1152-dim
> multimodal design in [multimodal-photo-search.md](multimodal-photo-search.md).

pgvector-backed approximate-nearest-neighbour (ANN) photo similarity, added
**additively** alongside the existing canonical embedding storage and exact-scan
search. The canonical store (`blob_embeddings.EmbeddingBytes`) remains the source
of truth and the fallback; pgvector is an acceleration layer.

Read the profile lifecycle first: [ai-photo-profile-lifecycle.md](ai-photo-profile-lifecycle.md).
Stable rules: [CLAUDE.md](../CLAUDE.md).

## What this adds

- A **dimension-specific** vector table `blob_embedding_vectors_768` holding the
  768-dim SigLIP2-base vectors, with an **HNSW cosine** index
  (`vector_cosine_ops`, `m=16`, `ef_construction=64`).
- `PhotoVectorIndexService` — a raw-SQL gateway (the `vector` type is **not**
  mapped in EF) that: detects availability, validates dimension + finiteness,
  syncs canonical embeddings into the vector table (idempotent, profile-keyed),
  counts coverage, and runs the owner-private ANN query.
- `PhotoSimilarityService` now **prefers the ANN path** when the active profile
  is vector-indexed, and **falls back to exact-scan** otherwise — same
  owner-private result shape either way.
- CLI `ai photos embeddings vector-sync` + vector lines in `coverage`.

## Storage strategy — why a per-dimension table

pgvector `vector(N)` columns are **fixed-dimension**, and an HNSW index is built
for one dimension. Profiles can differ in dimension (768 SigLIP base, 1152
so400m, 32 deterministic), so a single global vector column would be wrong.

This version ships **one table for 768** (`blob_embedding_vectors_768`). A
profile whose dimension is not 768 is **rejected/skipped** by vector-sync
(`unsupported-dimension`) and served by exact-scan — never truncated or padded.

**Adding another dimension later** (e.g. so400m 1152) is purely additive:

1. New migration: `CREATE TABLE blob_embedding_vectors_1152 (... embedding vector(1152) ...)` + its HNSW index, inside the same pgvector-availability guard.
2. Teach `PhotoVectorIndexService` to pick the table by dimension (a small
   dimension→table map); the dimension `768` constant becomes a lookup.
3. `vector-sync --profile <1152-profile>` populates it; similarity uses it
   automatically once the active profile points there.

No redesign, no change to canonical storage, no cross-dimension mixing.

## PostgreSQL image / extension

Production + dev compose now use **`pgvector/pgvector:pg17`** (same MAJOR
version as the previous `postgres:17-alpine`, so the data volume is compatible —
**no dump/restore**). The `AddPhotoVectorIndex768` migration runs:

```sql
CREATE EXTENSION IF NOT EXISTS vector;   -- only if the build offers it
CREATE TABLE IF NOT EXISTS blob_embedding_vectors_768 (...);
CREATE INDEX ... USING hnsw (embedding vector_cosine_ops) WITH (m=16, ef_construction=64);
```

The migration is **fault-tolerant**: the whole block is guarded by a check on
`pg_available_extensions`. On a **non-pgvector** image (e.g. the old
`postgres:17-alpine`, or the CI integration container) it logs a `WARNING` and
**skips** the table — the migration never fails, and similarity transparently
uses exact-scan. So the migration is safe to apply before/after the image swap.

### ⚠️ libc / collation note (one-time, on the server)

`postgres:17-alpine` uses **musl**; `pgvector/pgvector:pg17` uses **glibc**.
Switching the base image changes the default collation provider. The data is
compatible, but Postgres may warn about a **collation version mismatch** on
existing text indexes after the first start on the old volume. If it does:

```sql
-- inspect, then refresh affected collations / rebuild text indexes:
REINDEX DATABASE nubarca;            -- or target specific indexes
ALTER DATABASE nubarca REFRESH COLLATION VERSION;
```

This is a one-time maintenance step, not a data risk. Back up first (per the
deploy runbook).

## Operator workflow

```bash
# 0) Deploy the pgvector image + apply migrations (creates the vector table).
#    (Back up the DB first; migrations are additive and manual.)

# 1) Index an existing profile's embeddings into pgvector (idempotent, 768-dim):
ai photos embeddings vector-sync --profile photo-siglip2-base-patch16-384-v1 --dry-run
ai photos embeddings vector-sync --profile photo-siglip2-base-patch16-384-v1

# 2) Verify coverage (embedding AND vector):
ai photos embeddings coverage --profile photo-siglip2-base-patch16-384-v1
#   ... vector_supported=True vector_indexed=<n> missing_vectors=0 vector_coverage_percent=100

# 3) Make it the active profile (lifecycle), then restart api/worker:
#   .env: Ai__PhotoSimilarityProfileKey=photo-siglip2-base-patch16-384-v1
```

`vector-sync` output (aggregate, sanitized — never raw vectors / ids / paths):

```text
profile=<key>
dimension=<dim>
eligible_embeddings=<count>     # canonical embeddings for the profile
vector_indexed=<count>          # already in pgvector
missing_vectors=<count>
synced=<count>                  # inserted this run
skipped_dimension_mismatch=<count>   # decoded length != table dim (never padded)
failed=<count>                  # NaN/Infinity/zero-norm or insert error
dry_run=<true|false>
vector_backend=available | pgvector-unavailable | unsupported-dimension
```

## Read path (similarity)

1. Resolve the active profile (lifecycle: override > config > default fallback).
2. Load the **query** vector from canonical `blob_embeddings` (profile-keyed).
3. If the profile is **vector-indexed** (`≥1` row in its dimension's table):
   run the HNSW ANN query — `ORDER BY embedding <=> @q` (cosine), with the
   owner filter (`OwnerUserId`, `DeletedAt IS NULL`, exclude the query file)
   pushed into the SQL so the limit applies to the caller's own files only.
   Score returned is `1 - cosine_distance` (cosine similarity), rounded.
4. Otherwise fall back to the exact-scan over `EmbeddingBytes` (unchanged).

Profiles are **never mixed**: every read/write filters by `ProfileId`.

### Recall / tuning

HNSW recall under the owner filter is governed by `hnsw.ef_search` (default 40).
For owners with large libraries, raise it per-session for better recall:

```sql
SET hnsw.ef_search = 100;
```

A future change can `SET LOCAL hnsw.ef_search` inside the search transaction. The
`m`/`ef_construction` build params are conservative; rebuilding the index with
higher values trades build time/size for recall.

## Guarantees / non-leak

- No raw vectors, `BlobObjectId`, SHA, `StorageKey`, or physical paths in any
  API/CLI output — only logical FileItem id + name + rounded score, or counts.
- Owner-private only; no public-share behaviour change; no cross-owner search.
- Canonical storage and exact-scan are unchanged; pgvector is additive and
  optional. Deterministic (dim 32) similarity keeps using exact-scan.

## What remains to validate on the server

The pgvector path is covered by CI integration tests against a real
`pgvector/pgvector:pg17` container (sync, idempotency, dimension/NaN rejection,
ANN ordering, owner-privacy, fallback). On the production/lab host, after the
image swap + migration, validate:

- `docker compose ... up -d` starts cleanly on the existing volume; check logs
  for any collation-version warning and `REINDEX` if present.
- `ai photos embeddings coverage --profile <key>` shows `vector_supported=True`.
- A real `vector-sync` of the chosen profile reaches `missing_vectors=0`.
- `GET /api/files/{id}/similar` returns coherent owner-private neighbours.

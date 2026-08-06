# AI Substrate

NubArca is being built toward an *updateable* AI substrate (embeddings,
document text/OCR, faces, semantic + unified search) that can adopt new models
quickly. The full phased design lives on the `docs/ai-substrate-plan` branch;
this document tracks what is **actually implemented**.

## Status: Phase 0A — schema + configuration only

Phase 0A lands the persistence and configuration foundation **and nothing else**.
It is safe to deploy and is a no-op by default.

**Implemented**
- `AiOptions` configuration (section `Ai`), disabled by default.
- 13 domain entities + EF Core configurations for the substrate.
- `DbSet<>` registrations on `AppDbContext`.
- Additive migration `AddAiSubstrate` (new tables only; no changes to existing
  storage/file/refcount tables).
- Tests for configuration defaults/binding and schema/constraint behaviour.

**Explicitly NOT in Phase 0A** (later phases): real AI inference, ONNX, external
API calls, pgvector, vector search, embedding generation, backfill job handlers,
CLI commands, UI, and any public-share behaviour change.

### Configuration (`Ai` section)

AI is **off by default**. Defaults:

| Key | Default |
|---|---|
| `Ai:Enabled` | `false` |
| `Ai:Provider` | `none` |
| `Ai:MaxConcurrency` | `1` |
| `Ai:TimeoutSeconds` | `30` |
| `Ai:ComputeSliceSeconds` | `30` |
| `Ai:ComputeSliceItemBudget` | `100` |
| `Ai:ImageEmbeddingsEnabled` … `Ai:TagsEnabled` | `false` |
| `Ai:Onnx:ModelDir` | *(empty)* |
| `Ai:External:BaseUrl` / `Ai:External:ApiKeyRef` | *(empty)* |

`Ai:External:ApiKeyRef` is the **name/reference** of a secret, never the secret
itself. The `Onnx`/`External` blocks are bound now but unused until a later phase.

### Schema (tables created by `AddAiSubstrate`)

Registry/versioning: `ai_models`, `ai_profiles`. Per-blob status:
`blob_ai_artifact_statuses`. Embeddings (provider-agnostic `byte[]`, **no
pgvector**): `blob_embeddings`, `document_chunk_embeddings`, `face_embeddings`.
Documents: `document_texts`, `document_chunks`. Faces: `face_detections`,
`person_groups`, `face_assignments`. Annotations: `ai_annotations`. Diagnostics:
`ai_index_diagnostics`.

Key invariants baked into the schema:
- **Every AI output is keyed by `ProfileId`** so models/dimensions can be
  reindexed without disturbing existing outputs.
- **Implicit pending**: a missing `blob_ai_artifact_statuses` row means "not
  processed yet". Rows are written only for terminal states (`completed` /
  `skipped` / `failed`); profiles never pre-materialise pending rows. `skipped`
  is content-related and permanent only — never an unavailable-provider signal.
- **Owner ownership + isolation**: document/face/annotation rows are
  owner/file-scoped; `person_groups` and `face_assignments` are owner- and
  profile-scoped (no cross-owner clustering).
- **`face_assignments` carries an explicit `FaceEmbeddingProfileId`** with a
  unique index on `(OwnerUserId, FaceDetectionId, FaceEmbeddingProfileId)`, so a
  v1→v2 face-embedding reindex keeps both clusterings and rollback is a
  default-profile flip.
- **`ai_index_diagnostics` is generic** (blob / document-chunk / face-detection /
  owner-scoped clustering / annotation / provider) and aggregate-only — it never
  stores file names, text, vectors, storage keys, blob SHA, raw payloads, stack
  traces, or secrets.
- Derived rows **cascade** from their source blob/file; profiles/owners are
  `Restrict` (an output never deletes a profile or user).

### Migration / deploy notes
- `AddAiSubstrate` is purely additive and does **not** require pgvector; it
  applies on the current `postgres:17-alpine` image and on the SQLite test DB.
- Apply with the existing `db migrate` CLI (or `Database:MigrateOnStartup`).
- Rollback: the migration's `Down` drops only the new AI tables.
- Disable path: AI is already inert with defaults; no flag change is needed.

## Status: Phase 0B — service abstractions + provider resolution + deterministic backend

Phase 0B adds the service layer over the Phase 0A schema. Still **no real AI
inference, no pgvector, no ONNX, no external API calls, no jobs, no UI**, and AI
is still disabled by default.

**Implemented**
- Backend contracts (`Ai/Backends`): `IAiBackend` + capability interfaces
  `IImageEmbedder`, `ITextEmbedder`, `ITextExtractor`, `IFaceDetector`,
  `IFaceEmbedder`, `IImageCaptioner`, `IAiTagger`, with raw result records.
- Two providers:
  - **`none`** (`NoneAiBackend`) — the default; serves no capability, so any
    none-provider profile resolves as *unavailable*. This is an environment/
    config state, **not** a content failure: callers do nothing and never write
    per-blob `skipped`/`failed` status rows.
  - **`deterministic`** (`DeterministicAiBackend`) — **dev/test infrastructure
    only**. Stable, reproducible, non-semantic embeddings from input bytes/
    strings (SHA-256 → SplitMix64 → unit-normalized vector), salted per
    capability so image vs face vs text differ. No model file, no network.
- **Profile-driven resolution** (`IAiBackendResolver`): the provider is decided
  by the profile's model (`AiModel.Provider`), never a single global setting.
  The resolver answers available/unavailable (with a sanitized reason), the
  serving provider, expected dimension, and distance metric — and returns
  unavailable results instead of throwing (AI disabled, no default profile,
  profile/model disabled, provider none/unavailable, capability unsupported).
- **Profile/model registry** (`IAiProfileRegistry`): list models/profiles,
  resolve the default profile per capability, resolve by stable key, validate
  profile/backend compatibility, and an **explicit** dev/test seeder
  (`SeedDeterministicProfilesAsync`) — never run on startup.
- **Vector byte utilities** (`IAiVectorSerializer`): stable float32-LE encode/
  decode for the `byte[]` embedding columns; validates dimension, rejects
  NaN/Infinity, and offers cosine-friendly normalization. Internal only — raw
  vectors are never exposed through any API/CLI.
- **Diagnostics** (`IAiDiagnosticsWriter` + `AiDiagnosticSanitizer`): writes
  aggregate-only `provider`-target diagnostics. The API takes only a controlled
  reason *code* (no exceptions/messages/payloads), and the sanitizer collapses
  newlines + truncates as a second line of defence. Phase 0B does not auto-emit
  diagnostics on resolution (no spam).
- **Status** (`IAiStatusService`): a lightweight snapshot (enabled flag, default
  provider, model/profile counts, per-capability availability). Exposes profile
  **stable keys** only — never GUIDs, raw vectors, SHA, storage keys, or paths.
- DI: `services.AddAiSubstrate()` registers the graph in the web host, the
  CLI/worker host, and the test fixture identically (inert by default).

**Behaviour**
- `Ai:Enabled=false` (default) → every capability resolves *unavailable*
  (`ai-disabled`).
- A profile/model pointing at the `none` provider (or any missing/unavailable
  backend) → *unavailable*, never an exception, and **no** per-blob status rows.
- The deterministic backend is only ever reached when a profile's model
  provider is explicitly `deterministic`.

## Status: Phase 0C — operations skeleton (CLI / admin status / skeleton jobs)

Phase 0C makes the substrate **observable and operable** without performing any
real AI inference. AI is still disabled by default; there is still no pgvector,
ONNX, external provider call, real embedding/extraction/detection/tagging, photo
similarity, or UI.

**Implemented**
- **CLI** (`ai` verb, mirrors the existing operator CLI):
  - `ai status` — enabled flag, default provider, model/profile counts,
    per-capability availability, and an aggregate diagnostics total.
  - `ai models` / `ai profiles` — registry listings by **stable key** (model
    key, provider, capability/modality, version, enabled, dimension, metric;
    profiles also show default/enabled). No GUIDs.
  - `ai diagnostics` — aggregate-only counts grouped by capability / target kind
    / profile key / error code / permanence, with the latest timestamp.
  - `ai seed` — explicit, idempotent deterministic dev/test seeding (see below).
  - `jobs enqueue ai-…-backfill [--profile <key>] [--dry-run]` for all seven AI
    jobs.
- **Admin API** (admin-only, aggregate/status only): `GET /api/admin/ai/status`
  and `GET /api/admin/ai/diagnostics`, mirroring the storage-stats / admin-jobs
  endpoints (`RequireAuthorization("Admin")`). DTOs carry stable keys + counts
  only — never raw vectors, blob ids, SHA, storage keys, paths, payloads, stack
  traces, extracted text, or face identity.
- **Skeleton jobs** (Compute band, priority 200), registered + enqueueable:
  `ai.photos.embeddings.backfill`, `ai.documents.extract.backfill`,
  `ai.documents.embeddings.backfill`, `ai.faces.detect.backfill`,
  `ai.faces.embeddings.backfill`, `ai.faces.cluster.backfill`,
  `ai.tags.generate.backfill`. They share one flag-only `AiBackfillJobPayload`
  (optional profile **key**, never a GUID) and a versioned `AiBackfillCheckpoint`
  (defined for Phase 1; 0C handlers do not continue).

**Skeleton job behaviour (Phase 0C performs NO inference)**
- `Ai:Enabled=false` → no-op complete; no diagnostic, no rows.
- capability flag off → no-op complete; no diagnostic, no rows.
- profile resolves (deterministic seeded) → no-op complete (`skeleton-noop`); no
  rows written — Phase 1 will do the real work here.
- provider unavailable / no default profile → no-op complete **and at most one
  aggregate transient `provider` diagnostic**; never per-blob `skipped`/`failed`
  rows, never pending rows.
- cancellation → no-op; never a permanent diagnostic / permanent failure.
- never writes `BlobEmbedding` / `DocumentText` / `DocumentChunk` /
  `DocumentChunkEmbedding` / `FaceDetection` / `FaceEmbedding` / `AiAnnotation`
  rows, and never touches files, imports, downloads, or thumbnails.

**Deterministic / dev seed (`ai seed`)** — seeds a single `deterministic-v1`
model + one default profile per capability, all clearly dev/test (not real
semantic AI). Idempotent, never run on startup, and does **not** enable
inference or AI globally (you still must set `Ai:Enabled` + the capability flags
to make a capability *resolve*, and even then Phase 0C jobs no-op).

### Next phase
Phase 1: **photo similarity v0** — deterministic image embeddings written by a
real `ai.photos.embeddings.backfill` over actual image candidates (still no
pgvector; exact-scan similarity), plus an owner-private similarity endpoint.

## Plates ALPR is a SEPARATE pipeline (not People/Face)

The Plates (Targhe) extension's license-plate recognition is its **own**
owner-private pipeline, run from the **worker**. It **must not** reuse the People
identity model, face embeddings, face clustering, or the `person_groups`/
`face_assignments` substrate, and it must never surface through any public /
Party / TV / People surface. All plate-derived data is owner-private (see the AI
product rule in `CLAUDE.md`).

## Plates — capabilities

Plates is a self-contained, owner-private surface with its own configuration,
profile keys and tables. It is deliberately **not** part of the People identity
model, and none of its data may surface through any public / Party / TV / People
path.

**Secure container.** Upload, list, detail, preview, original and delete, over a
hidden owner-scoped logical container key. No AI is involved in this path.

**ALPR.** Detection + OCR + `plate_detections` persistence, driven by the
`plates.analyze` worker job (Compute band). It uses a dedicated `Plates:Alpr`
config and `ProfileKey` (`plate-alpr-v1`), **completely separate** from the AI
substrate's `AiModel`/`AiProfile` registry and the face profiles: it does not
touch `blob_ai_artifact_statuses`, `face_detections`, `face_embeddings`,
`person_groups` or `ai_annotations`. Disabled by default; when unconfigured a run
records a safe `model_not_configured` outcome — an environment state, never a
content `skipped`/`failed`. An in-process ONNX detector+OCR runner is available
behind `Plates:Alpr:Provider=Onnx` with documented tensor contracts and safe
missing/unsupported-model errors.

**Privacy-only face redaction.** `blurFaces=true` on preview/original/thumbnail,
backed by a derived redacted-media cache (`plate_face_redaction_boxes` +
`plate_redacted_media`). This is **NOT identity**: it detects face *regions* only
in order to blur them, and creates no `face_detections` / `face_embeddings` /
`face_clusters` / `people` / `person_face_assignments` rows, uses no People
embeddings and produces no cross-owner data. It has its own
`Plates:FaceRedaction` config and `ProfileKey` (`plate-face-redaction-v1`),
entirely separate from `Ai:Face*`, People and `Party:FaceSearch*`. Redaction
metadata is owner-private PlateImage metadata; boxes are never exposed through any
DTO or API, because redaction is baked into the served media. Disabled by default;
`blurFaces=true` while disabled returns a safe `face_redaction_not_configured`
error and **never** the unredacted image.

**Reusing the face detector for boxes only.** With
`Plates:FaceRedaction:Provider=ExistingNubArcaFaceDetector`,
`ExistingNubArcaPlateFaceBoxDetector` resolves the face profile via
`FaceProfileResolver` / `IAiBackendResolver` and calls the SCRFD backend's
detection-only `IFaceDetector.DetectFacesAsync` — the evaluation-only path that
returns normalized boxes and **persists nothing** (`OnnxFaceBackend` writes no
`face_detections`/`face_embeddings` and creates no clusters or identities). The
adapter takes bbox + score only, drops landmarks, never resolves an embedder and
never touches the People/Face tables. So Plates redaction reuses the effective
detector while staying fully separate from People identity, and Plates commits no
model weights of its own.

Both providers default **disabled**; see
[model-deployment/plates.md](model-deployment/plates.md).

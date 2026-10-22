# Document Intelligence v1 — deployment runbook

How to turn the owner-document pipeline on in production, one phase at a time,
and how to turn it off again. It assumes the release that contains it has
already been deployed **with everything off**.

Ground rules. None of them is new; they are repeated because this runbook is
where they are easiest to forget:

- **Read [`deploy/FAST_DEPLOY.md`](../../deploy/FAST_DEPLOY.md) immediately
  before any production command.** Releases go through the guided
  `deploy/update-production.sh check|apply` path, including the migration below.
  Never add `--confirm-migrations` by hand.
- **Never `source` the production `.env`.** Compose reads it through
  `--env-file .env`. To read one value, use `grep '^KEY=' .env`.
- Host, login, checkout path and model directory belong to the installation.
  They come from the operator and never go into tracked source.
- Every key below defaults to off, empty or the code default. Nothing here
  downloads weights at runtime or calls a hosted service.

```bash
ssh "$NUBARCA_PRODUCTION_SSH"
cd "$NUBARCA_PRODUCTION_CHECKOUT"
DC="docker compose -f docker-compose.prod.yml -f docker-compose.prod.local.yml -f docker-compose.facedirect-api.yml -f docker-compose.release.local.yml --env-file .env"
```

## 1. What ships, and where each key is read

The tracked `docker-compose.prod.yml` forwards these keys. Before this change it
did not, so a value in `.env` never reached a container.

| Key (container) | `.env` variable | api | worker | Default |
|---|---|---|---|---|
| `Ai__Enabled`, `Ai__DocumentExtractionEnabled`, `Ai__DocumentEmbeddingsEnabled` | same | yes | yes | `false` |
| `Ai__DocumentExtraction__OcrEnabled` | same | yes | yes | `false` |
| `Ai__DocumentExtraction__OcrLanguages` | same | yes | yes | `eng` (code default) |
| `Ai__DocumentExtraction__OcrPageTimeoutSeconds`, `__MaxConcurrentOcrPages`, `__ParseTimeoutSeconds` | same | yes | yes | 30, 1, 120 |
| `Ai__DocumentIndexing__{MaxConcurrentJobs,GateWaitSeconds,MaxAttemptsPerSource,ParallelTextAndVisualModels}` | same | yes | yes | 1, 10, 3, `false` |
| `Rag__Domains__user-documents__SemanticEnabled` | `Rag__Domains__user_documents__SemanticEnabled` | yes | yes | `false` |
| `Rag__Domains__user-documents__TextEmbeddingProfileKey` | `Rag__Domains__user_documents__TextEmbeddingProfileKey` | yes | yes | empty |
| `Ai__Onnx__OpenVino__RagTextDevice` | same | yes | yes | `CPU` |
| `Ai__DocumentVisual__*` (Enabled, RenderOfficeEnabled, socket, late interaction) | same | yes | yes | off/empty (already forwarded) |
| `Assistant__PrivateKnowledgeModel` | same | yes | **no** | empty |
| `Assistant__Models__documents-local__*` | `Assistant__Models__documents_local__*` | yes | **no** | empty; Protocol `OpenAiCompatible` |

Why both services: the worker runs `ai.documents.index` (extract → OCR → chunk
→ embed → visual). The api embeds each question, runs the `documents` CLI
(`$DC exec api …`) and, with `Jobs__WorkerEnabled=true`, the in-process job
runner. The private chat endpoint is api-only, so the worker never holds the
model endpoint or its key.

Spelling: a hyphen cannot appear in a Compose interpolation name, so the `.env`
variable writes `user_documents` and `documents_local`. Compose maps each one to
the real container key. This is the same convention the Help profile uses
(`Assistant__Models__help_default__*`).

The values are set in the BASE file. The installation's untracked
`docker-compose.prod.local.yml` is stacked after it and still wins. For the same
reason, `RagTextDevice` is not in `docker-compose.facedirect-api.yml`, which is
stacked after the local file.

`tests/NubArca.Api.Tests/Deployment/DocumentPipelineComposeWiringTests.cs`
checks this table against the tracked file, and binds an empty `.env` through
the real option classes to prove it means "off".

## 2. Before the release that carries this wiring

Keys that used to be inert become live once this compose file is deployed.
Look for stale values first:

```bash
grep -nE '^(Ai__Document|Rag__Domains__user_documents__|Assistant__PrivateKnowledgeModel|Assistant__Models__documents_local__|Ai__Onnx__OpenVino__RagTextDevice)' .env
grep -nE 'Ai__Document|Rag__|PrivateKnowledgeModel|RagTextDevice' docker-compose.prod.local.yml
```

Expected result: nothing, or only values you mean to keep. If the local
override already sets one of these keys, it keeps winning. Decide whether to
move the value into `.env` and delete it from the override.

The release also carries the migration
`20261021224250_AddDocumentSourceIdentityAndIndexState`. Its policy entry in
[`deploy/migration-policy.json`](../../deploy/migration-policy.json) is
`automated: true`, `previousApplicationCompatible: true`, so `check` prints
`--confirm-migrations` and `apply` takes the verified pre-migration backup
itself. Deploy it through the guided path, with every pipeline key still off.

After `apply`, confirm the keys arrived and are off. This prints only
non-secret keys:

```bash
for c in nubarca-api nubarca-worker; do
  echo "== $c"
  docker inspect "$c" --format '{{range .Config.Env}}{{println .}}{{end}}' |
    grep -E '^(Ai__Enabled|Ai__Document|Rag__Domains__|Ai__Onnx__OpenVino__RagTextDevice|Assistant__PrivateKnowledgeModel)='
done
```

## 3. Changing configuration between phases

A phase change is a **configuration change on the pinned images**, not a
release. Edit `.env` and recreate the two application services. Do not use
`--build`, do not run `down`, and do not touch the release pins:

```bash
$EDITOR .env          # never `source` it
$DC up -d --no-build --no-deps api worker
curl -fsS http://127.0.0.1:8080/health/ready
```

Then run the `docker inspect` check from §2 again, and the health checks in §6.

## 4. Model checkpoints

Models live under the directory that `docker-compose.prod.local.yml` mounts
read-only at `/models/ai` (`Ai__Onnx__ModelDir`). Find its host side with:

```bash
grep -n '/models/ai' docker-compose.prod.local.yml
```

Below it is called `MODEL_HOST_DIR`. Set it in your shell from that output, and
never write it into source.

| Profile key | Directory under `MODEL_HOST_DIR` | Files | Source at pinned revision |
|---|---|---|---|
| `rag-text-multilingual-e5-small-v1` (default) | `multilingual-e5-small/` | `model.onnx`, `tokenizer.json` | `intfloat/multilingual-e5-small` @ `614241f622f53c4eeff9890bdc4f31cfecc418b3` (`onnx/model.onnx`, `tokenizer.json`) |
| `rag-text-granite-embedding-97m-multilingual-r2-v1` | `granite-embedding-97m-multilingual-r2/` | `model.onnx`, `tokenizer.json` | export script, pinned `835ad140…` |
| `rag-text-granite-embedding-311m-multilingual-r2-v1` | `granite-embedding-311m-multilingual-r2/` | `model.onnx`, `tokenizer.json` | export script, pinned `44399559…` |
| `rag-text-qwen3-embedding-0.6b-{1024,768}-v1` | `qwen3-embedding-0.6b/` | `model.onnx`, `model.onnx.data`, `tokenizer.json` | export script, pinned `97b0c614…` |
| `document-visual-siglip2-so400m-patch14-384-v1` | `siglip2-so400m-patch14-384/` | `model.onnx`, `text_model.onnx`, `tokenizer.json` | already installed for photos; shared |

The full revisions, and why each candidate needs what it needs, are in
[`docs/model-deployment/text-embeddings.md`](../model-deployment/text-embeddings.md).

Install procedure:

1. **Produce the files off-production.** Use the target server or a similar
   machine, outside every container:
   `python3 scripts/export-text-embedding-onnx.py --model <catalog key> --output <staging>`.
   The script downloads only the pinned revision, verifies parity
   (cosine ≥ 0.999) and prints the SHA-256 of every file. For e5,
   `scripts/document-bench/fetch-models.sh --only e5-small` fetches the pinned
   revision and writes `FETCHED.json` with the SHA-256 of every file.
2. **Record the hashes** in a `SHA256SUMS` file next to the model directory, on
   the host, outside the repository.
3. **Copy into a sibling staging directory on the same filesystem**, then
   verify the hashes and rename. A rename is atomic, so a half-copied model is
   never visible under the real name:

   ```bash
   cd "$MODEL_HOST_DIR/.staging-qwen3-embedding-0.6b" && sha256sum -c SHA256SUMS
   mv "$MODEL_HOST_DIR/.staging-qwen3-embedding-0.6b" "$MODEL_HOST_DIR/qwen3-embedding-0.6b"
   cd "$NUBARCA_PRODUCTION_CHECKOUT"
   ```

4. **Seed and validate.** This writes `AiModel`/`AiProfile` rows only, with
   `IsDefault=false`:

   ```bash
   $DC exec -T api dotnet NubArca.Api.dll rag seed-profiles
   $DC exec -T api dotnet NubArca.Api.dll rag validate-model --profile <profile key>
   ```

   For 768-d and 1024-d profiles, `vector_table=FAILED` is expected. The
   owner-private domain uses exact in-process cosine and no pgvector table.
   Product Help must stay on a 384-d profile.
5. **Replacing the files of a model that is already loaded needs
   `$DC up -d --no-build --no-deps api worker`.** ONNX sessions are cached for
   the life of the process and are never reloaded.

## 5. Configuration per phase

Each phase is accepted (§9) before the next one starts. Run the backfill per
owner, starting with one pilot owner. Use `--all-owners` only after the pilot
has passed.

**Phase 1: extraction and lexical search.** Text only: no model, no OCR.

```text
Ai__Enabled=true
Ai__DocumentExtractionEnabled=true
```

`Ai__Enabled` is the AI master switch. On an installation that already runs
photo or face AI it is already `true`. Every other capability keeps its own
flag, so this turns nothing else on.

From now on, **every owner's** uploads, restores and admin imports signal the
indexing job. Existing libraries are indexed only on request:

```bash
$DC exec -T api dotnet NubArca.Api.dll documents backfill --owner <pilot user id>
$DC exec -T api dotnet NubArca.Api.dll documents progress --owner <pilot user id>
```

**Phase 2: semantic.** Install and validate the model (§4) first.

```text
Ai__DocumentEmbeddingsEnabled=true
Rag__Domains__user_documents__SemanticEnabled=true
Rag__Domains__user_documents__TextEmbeddingProfileKey=rag-text-multilingual-e5-small-v1
# optional, after the target benchmark: Ai__Onnx__OpenVino__RagTextDevice=GPU
```

Then run `documents backfill --owner …` again. Extraction short-circuits, and
the embed phase runs for chunks that have no vector under this profile. A
candidate profile (Qwen3, Granite) is chosen only from the target-server
benchmark ([`benchmark.md`](benchmark.md) §4). It carries a placeholder
evidence floor of 1.0 until it is calibrated. With a larger model, check
`Ai__TimeoutSeconds` (30 s per embedding call) against the measured passage
latency.

**Phase 3: OCR.** Tesseract ships in the image (`eng`, `ita`).

```text
Ai__DocumentExtraction__OcrEnabled=true
Ai__DocumentExtraction__OcrLanguages=eng+ita
```

`documents status --owner …` reports OCR readiness without paths. Pages that
needed OCR while it was off were left pending (`ocr-unavailable`), not failed, so a
plain backfill picks them up. Leave `MaxConcurrentOcrPages=1`. The bench showed
Tesseract hanging with OpenMP threads, see [`benchmark.md`](benchmark.md) §6.

**Phase 4: visual.**

```text
Ai__DocumentVisual__Enabled=true
```

```bash
$DC exec -T api dotnet NubArca.Api.dll documents visual-seed-profiles
$DC exec -T api dotnet NubArca.Api.dll documents visual-status --owner <pilot user id>
```

This re-renders and re-embeds every document of every owner who is
backfilled. That is real CPU time and real storage. Without the Office renderer
(§8), PDFs and text/Markdown get visual search, and Office files stay
text-only (`visual-format-unsupported`, never permanent).

**Phase 5: private chat (optional).** Needs a model endpoint that the operator
runs and trusts. It is not part of this stack.

```text
Assistant__Enabled=true
Assistant__PrivateKnowledgeModel=documents-local
Assistant__Models__documents_local__Trust=LocalTrusted
Assistant__Models__documents_local__BaseUrl=<operator's own endpoint>
Assistant__Models__documents_local__Model=<model id>
```

An External profile is refused for this use when the configuration is read.
`Assistant__Enabled=true` also enables Help, if `Assistant__HelpModel` names a
complete profile.

## 6. Health checks

All of these are read-only. Their output carries counts and reason tokens:
no names, no ids, no paths.

```bash
$DC exec -T api dotnet NubArca.Api.dll ai status
$DC exec -T api dotnet NubArca.Api.dll rag domains
$DC exec -T api dotnet NubArca.Api.dll rag validate-model --domain user-documents
$DC exec -T api dotnet NubArca.Api.dll documents progress                    # installation aggregate
$DC exec -T api dotnet NubArca.Api.dll documents progress --owner <id>
$DC exec -T api dotnet NubArca.Api.dll documents status --owner <id>
$DC exec -T api dotnet NubArca.Api.dll documents visual-status --owner <id>
$DC exec -T api dotnet NubArca.Api.dll jobs list
docker stats --no-stream nubarca-api nubarca-worker
```

For a signed-in owner, `GET /api/assistant/documents/status` and
`GET /api/documents/indexing/status` show the same state, scoped to that owner.
The Documenti page uses them.

What healthy looks like:

- `automatic_indexing=enabled` from phase 1 on;
- `embeddings_in_job=enabled` and `semantic_enabled=true` with the expected
  `embedding_profile` from phase 2 on;
- phase counts that move to `completed` or to an understood `skipped` reason;
- `failed` that does not grow;
- no `provider_unavailable` diagnostic once the model is installed.

## 7. Recovery

- **A job died or the worker restarted.** Leases expire after
  `Jobs__LeaseSeconds`, and the job resumes from its checkpoint. Completed
  phases short-circuit, so nothing has to be cleaned up.
- **Sources stuck after retryable failures** (parse timeout, OCR process,
  storage read). After `MaxAttemptsPerSource` attempts, automatic runs stop
  retrying a source until its content changes. Fix the cause, then:

  ```bash
  $DC exec -T api dotnet NubArca.Api.dll documents backfill --owner <id> --retry-failed
  ```

- **A model was missing or broken.** The affected sources stay `pending` with a
  reason; that is an environment state. Install or fix the model (§4), recreate
  api and worker, and run a plain backfill.
- **Cancellation** never records a permanent failure. Run the backfill again.

## 8. Office renderer: not in the release stack (proposal)

`docker-compose.document-renderer.yml` (LibreOffice in a network-less
container) stays **outside** the mandated four-file `$DC`. This change does not
add a fifth file. Proposal, for a decision:

**Keep it out for v1.** The reasons:

- **It would break the release contract.** The file has a `build:` recipe, and
  the renderer image is not built, signed or digest-pinned by CI. The
  production stack deliberately carries no build recipe (`FAST_DEPLOY.md` §2),
  and the updater's Compose gate and rollback assume exactly
  `api`/`worker`/`frontend` pinned by digest.
- **It makes the api depend on it.** The file adds
  `depends_on: document-renderer` to api and worker. If the renderer image is
  missing, the api is held back with it.
- **What it adds is small for v1.** Office documents are fully extracted and
  searchable from their text without it; only their *visual* retrieval needs it.
- **It costs memory.** Up to 2 GB (its compose limit) for LibreOffice, on top
  of the AI budget.

**What adding it properly would take** (a separate, reviewed change):

1. a CI-built, signed renderer image, pinned by digest in the release manifest
   and in `docker-compose.release.local.yml`;
2. an explicit opt-in in `.env` (for example
   `NUBARCA_DOCUMENT_RENDERER_ENABLED=true`) that makes `update-production.sh`
   append the file, the same way `NUBARCA_API_VARIANT=openvino` appends
   `docker-compose.facedirect-api.yml`;
3. the file without `build:` in production, and the CLAUDE.md / FAST_DEPLOY
   `$DC` text updated in the same change.

Until then, an operator who wants Office visual search can run the renderer as
the documented experimental fifth file. It is then outside the guided
update/rollback path, and they own that difference. The five-file stack renders
(`docker compose config`); the api and worker gain only the socket volume.

## 9. Rollback

Rollback goes in the reverse order of the phases. Every step is a
configuration change (§3) unless it says otherwise.

| To undo | Do | Data left behind |
|---|---|---|
| Private chat | `Assistant__PrivateKnowledgeModel=` (or `Assistant__Enabled=false` if Help is not used) | none |
| Visual | `Ai__DocumentVisual__Enabled=false` | visual units and vectors stay, inert; rebuildable |
| OCR | `Ai__DocumentExtraction__OcrEnabled=false` | text already OCR'd stays until its source is re-extracted |
| A new embedding profile | set `…TextEmbeddingProfileKey` back to the previous profile | vectors are keyed by profile: the old ones are still there, so no re-embed is needed. The new profile's vectors stay inert |
| Semantic | `Rag__Domains__user_documents__SemanticEnabled=false`, `Ai__DocumentEmbeddingsEnabled=false` | lexical search keeps working |
| Everything | `Ai__DocumentExtractionEnabled=false` | no new indexing; existing rows stay searchable for their owners |

Every document row is **derived** from the original blobs, which are never
touched. Each one can be rebuilt by re-indexing:

- `document_texts`, `document_chunks` and `document_chunk_embeddings` (text,
  chunks, and vectors per profile);
- `document_source_states` (per-phase state; a missing row means implicit
  pending);
- the document visual units and vectors.

They are owner-private and never leave the owner's scope.

**Schema.** The guided path does not reverse migrations, and a rollback should
not either. `20261021224250_AddDocumentSourceIdentityAndIndexState` is declared
compatible with the previous application, so an image rollback runs fine on
the upgraded schema. Its `Down()` exists, but it drops `document_source_states`
and the source columns and deletes non-file rows. Treat it as the manual
exception path of `FAST_DEPLOY.md` §4.3, with its own backup and review. It is
never part of a configuration rollback.

## 10. Pilot ingestion acceptance checklist

Use one pilot owner (the operator's own account) and a small known set: a
digital PDF, a scanned PDF, a DOCX, an XLSX and a PPTX, in Italian and in
English.

- [ ] Before phase 1, `ai status` and the photo/face/video job counts are
  recorded. After every phase they are unchanged. Photo similarity, face search
  and video playback still work. (These pipelines are not touched by any key
  here.)
- [ ] `documents progress --owner <pilot>`: every source reaches `completed`, or
  a `skipped`/`pending` reason you can explain, for example `encrypted-document` or
  `visual-format-unsupported`. Nothing is left `failed` without a known cause.
- [ ] `documents status --owner <pilot>`: the per-format counts match the set.
  `semantic_enabled` and `embedding_profile` match the phase.
- [ ] The Documenti page finds each document by a distinctive phrase in each
  language. PDF hits carry the right page; slides, sheets and sections carry
  their locator. A query in one language finds a document in the other (phase
  2 on).
- [ ] A question with no answer in the set returns no evidence (phase 5: no
  model call).
- [ ] **Isolation.** A second account sees none of the pilot's documents: not in
  search, not in chat, not in status counts.
- [ ] **No leak in logs.** A distinctive file name and a distinctive phrase from
  the pilot set do not appear in the api or worker logs:
  `docker logs --since 2h nubarca-worker 2>&1 | grep -cF '<phrase>'` prints `0`.
  Do the same for `nubarca-api`.
- [ ] **Memory.** `docker stats` for api and worker during the backfill stays
  within the budget below. Record the peak.
- [ ] **Rollback rehearsed.** Switch the phase's key off (§9), recreate, and
  confirm that search degrades as described. Then switch it back on.
- [ ] Only then: `documents backfill --all-owners`, in a quiet window.

## 11. Memory budget

Target: Intel 12th gen, 32 GB, **at most 20 GB for AI**. Every figure below is
an **estimate, to be measured** on the target server unless it names a source.
Nothing has been measured there yet.

| Component | Process | Resident (estimate) | Basis |
|---|---|---|---|
| .NET runtime + app | api, worker | ~0.4 GB each | estimate |
| SigLIP2 so400m text tower (photo search; document-visual queries) | api | ~2.8–3 GB | 2.8 GB of external data ([`rag-platform.md`](../rag-platform.md), Phase 0) |
| SigLIP2 so400m image tower (photo indexing; document-visual pages) | worker | ~1.7 GB | ~400M params, FP32 |
| Face detector + recognizer (existing) | api, worker | ~0.3–0.6 GB each | estimate |
| e5-small | api **and** worker | ~0.5 GB each | [`text-embeddings.md`](../model-deployment/text-embeddings.md) |
| Granite 97M / 311M | api and worker | ~0.4 / ~1.25 GB each | same |
| Qwen3-Embedding-0.6B FP32 | api and worker | ~2.4 GB each | same |
| Tesseract, one page at a time | worker (child process) | ~0.3 GB peak | 260 MiB peak in the cloud container (**not** target) |
| Parse and render buffers | worker | ≤ 64 MiB source + ≤ 40 MB page bitmap | configured bounds |
| Office renderer (if added) | own container | ≤ 2 GB | its compose limit |
| Private-chat LLM, 3–4B int4 | operator's endpoint | ~3–5 GB | by analogy with the NLU model (~3.0–3.5 GB, [`nlu-command-model.md`](../model-deployment/nlu-command-model.md)) |
| Subject cutout (existing, on request) | api | ~3.3 GB | [`subject-cutout.md`](../model-deployment/subject-cutout.md) |
| HumanAesExpert sidecar (existing, optional) | own container | ~4.8 GiB peak | measured, [`human-aesexpert.md`](../model-deployment/human-aesexpert.md) |

Document-visual reuses the photo SigLIP2 checkpoint. Sessions are cached by
model path, provider and device. If photos and documents resolve to the same
key, the weights are resident once; otherwise there is a second copy (+1.7 GB
worker, +2.8 GB api). **Measure this.**

Indicative totals (estimates):

| Scenario | api | worker | Other | Total |
|---|---|---|---|---|
| Today (photo + face) | ~3.8 | ~2.7 | — | ~6.5 GB |
| + phase 1–3 (lexical, OCR) | ~3.8 | ~3.1 | — | ~7 GB |
| + phase 2 with e5 | ~4.3 | ~3.6 | — | ~8 GB |
| + phase 2 with Qwen3 FP32 | ~6.2 | ~5.5 | — | ~11.7 GB |
| + phase 4 visual, sessions not shared | ~9.0 | ~7.2 | — | ~16.2 GB |
| + phase 5 LLM + renderer | | | ~6–7 | **~22–23 GB: over budget** |

What follows from that:

- Qwen3 FP32, unshared visual sessions, a local LLM, the renderer and the
  optional sidecars do not all fit together.
- Prefer a smaller encoder (e5 or Granite 97M) unless the benchmark shows a
  clear quality win. Run the LLM on another host, or do without the renderer.
- Keep `ParallelTextAndVisualModels=false`. It bounds the activation peak,
  though not residency.

The pilot (§10) records the real figures. Replace this table with them.

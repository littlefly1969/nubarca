# Aesthetics Lab — HumanAesExpert-1B model deployment

> **Status: opt-in, experimental, DISABLED by default.** The Aesthetics Lab
> (Laboratorio estetico) analyzes ONLY images an owner explicitly adds to it —
> never the gallery, never in bulk. The model is local, external (a sidecar),
> controlled, and batch-only. Nothing runs until an operator installs the
> weights, starts the sidecar, and sets `HumanAesExpert__Enabled=true`.

The lab lets an owner add images (from the gallery or by direct upload), then
explicitly request analysis. Each analysis becomes one durable background job on
the existing worker, which calls an internal-only Python sidecar running
**KwaiVGI/HumanAesExpert-1B** (MIT). Results are stored as versioned, normalized
metrics and shown in the owner-private web UI at `/lab/aesthetics`.

## Model

|                    |                                                          |
|--------------------|----------------------------------------------------------|
| Repository         | **`KlingTeam/HumanAesExpert-1B`** (the original `KwaiVGI/HumanAesExpert-1B` redirects here) |
| Immutable revision | **`b8f7ee3f3a1217ecd331fd6d57b6959f5c0da183`** (model + custom code) |
| License            | **MIT**                                                  |
| Base               | InternVL2-1B (InternViT + Qwen2)                          |
| Runtime            | `transformers==4.44.2` + `accelerate==0.34.2`, CPU-only `torch==2.4.1+cpu` / `torchvision==0.19.1+cpu`, Python 3.11 |
| On-disk            | `model.safetensors` 1,876,417,886 B (fp16); SHA-256 `61c232b0…3b10ec` |
| Enabled capability | `expert_scores` (12 Expert-head dimensions) only         |
| Score scale        | **[0, 1]** per dimension — verified from code (see below) |

All pinned files + SHA-256 are in the committed manifest
`scripts/human-aesexpert-sidecar/manifests/human-aesexpert-1b.sha256` (16 files),
which the fail-closed installer verifies before activating.

### Verified Expert-head mapping + scale (authoritative, from the pinned code)

Verified directly against the pinned checkpoint (revision above):
`modeling_internvl_chat.py` `expert_score()` returns the 12-element tensor with
this exact `names` list and ORDER, and `modeling_qwen.py` `Expert_Head.forward()`
ends with **`return F.sigmoid(pooled_expert_scores)`** — so every dimension is in
**[0, 1]** (not assumed). The 12 outputs, in tensor order, map to these stable
NubArca contract keys (see `AestheticMetricCatalog`):

| # | Model name (verbatim)                | Contract key                   | Group       |
|---|--------------------------------------|--------------------------------|-------------|
| 0 | Facial Brightness                    | `facial_brightness`            | face        |
| 1 | Facial Feature Clarity               | `facial_feature_clarity`       | face        |
| 2 | Facial Skin Tone                     | `facial_skin_tone`             | face        |
| 3 | Facial Structure                     | `facial_structure`             | face        |
| 4 | Facial Contour Clarity               | `facial_contour_clarity`       | face        |
| 5 | Facial Aesthetic Score (parent)      | `facial_aesthetic`             | face        |
| 6 | Outfit                               | `outfit`                       | appearance  |
| 7 | Body Shape                           | `body_shape`                   | appearance  |
| 8 | Looks                                | `looks`                        | appearance  |
| 9 | Environment                          | `environment`                  | environment |
| 10| General Appearance Aesthetic (parent)| `general_appearance_aesthetic` | appearance  |
| 11| Comprehensive Aesthetic Score (overall)| `overall_aesthetic`          | overall     |

`overall_aesthetic` is the item's headline score. Curated, localized labels live
in the frontend i18n (`aesthetics.metric.*`), never derived from the keys.

The three other capabilities (`score_head`, `meta_voter`, `text_assessment`) are
scaffolded in the domain/API/sidecar contract but **disabled by configuration**
(`HumanAesExpert__Allow*=false`) until separately benchmarked and validated.

## 1. Install the weights (operator step — never automatic)

Weights are **not** committed and **not** downloaded by the API/worker at
startup. The installer is **fail-closed**: it verifies every file against a
committed SHA-256 manifest, stages to a temp dir, and activates by atomic rename
(keeping the previous install for rollback).

```bash
# 1. Pin the exact commit SHA in the installer catalogue first:
#    scripts/install-human-aesexpert-model.sh  →  REVISION[human-aesexpert-1b]
# 2. Commit the checksum manifest (populated OUT-OF-BAND from the model host):
#    scripts/human-aesexpert-sidecar/manifests/human-aesexpert-1b.sha256
#    (see that folder's README.md for how to populate it safely)
pip install 'huggingface_hub[cli]'
HUMANAES_MODEL_KEY=human-aesexpert-1b \
HUMANAES_MODELS_ROOT=/srv/nubarca/models/human-aesexpert \
./scripts/install-human-aesexpert-model.sh
# → installs to /srv/nubarca/models/human-aesexpert/human-aesexpert-1b
```

A missing manifest, missing expected file, or ANY checksum mismatch aborts with
nothing activated. Rollback: the prior install is kept at `<key>.prev`.

**Storage location:** the model directory is mounted **read-only** into the
sidecar; it is never on the blob-store volume and the sidecar gets no blob mount.

## 2. Start the sidecar (additive Compose fragment)

The fragment is **additive** to the required production stack (never omit the
prod files — see CLAUDE.md). The ordinary stack without this fragment continues
to resolve and run.

```bash
HUMANAES_MODEL_DIR=/srv/nubarca/models/human-aesexpert/human-aesexpert-1b \
docker compose -f docker-compose.prod.yml \
               -f docker-compose.prod.local.yml \
               -f docker-compose.human-aesexpert.yml \
               --env-file .env up -d human-aesexpert
```

The `human-aesexpert` service: internal-only (no published ports; reachable from
api/worker as `http://human-aesexpert:8091`), read-only weight mount, read-only
rootfs, non-root user, `no-new-privileges`, bounded CPU/RAM, health-checked at
`/health`, warm model loaded once, inference concurrency 1, bounded queue (429
when full), per-request timeout → 504, NO blob-store mount, NO outbound network.
Transformers' regenerable dynamic-module cache (required by the checkpoint's
local `trust_remote_code` implementation) is confined to the `/tmp` tmpfs;
`HF_HUB_OFFLINE=1` and `TRANSFORMERS_OFFLINE=1` make loading fail closed instead
of attempting a model-hub fallback. No cache or generated code is written beside
the read-only weights or into the container root filesystem.

Readiness: `GET /ready` returns 200 only once the model is warm. Missing/broken
weights → the sidecar logs a load failure and reports not-ready; it does **not**
crash-loop. The worker then fails analysis runs with a safe `model_unavailable`
code (an environment state — never a permanent content skip).

### CPU and memory tuning

The fragment defaults to a portable 4-CPU/8-GiB limit. `cpus` is a quota, not
CPU affinity: on a hybrid Intel host Linux may spread that quota over P-cores,
E-cores and SMT siblings. Operators can tune without editing Compose:

```dotenv
HUMANAES_CPUSET=0,2,4,6,8,10
HUMANAES_CPU_LIMIT=6.0
HUMANAES_MEMORY_LIMIT=10g
HUMANAES_TORCH_THREADS=6
HUMANAES_TORCH_INTEROP_THREADS=1
```

The example is specifically one logical thread from each of the six P-cores on
an i7-12650H; CPU numbering must be verified with `lscpu -e` and thread sibling
lists before using it on another host. `OMP_NUM_THREADS` and `MKL_NUM_THREADS`
track `HUMANAES_TORCH_THREADS`. Benchmark with a synthetic image after changing
the values; more SMT threads do not necessarily improve single-run latency.

## 3. Enable the feature (api + worker)

Set on **api + worker** (in `.env`; never `source` it — see CLAUDE.md):

```
HumanAesExpert__Enabled=true
HumanAesExpert__SidecarBaseUrl=http://human-aesexpert:8091
```

**Worker requirement:** analysis runs on the dedicated jobs worker (Compute
band). The worker must be running (`$DC --profile worker up -d worker`) for
queued runs to execute; without it, runs stay `queued`.

All settings (`HumanAesExpert:*`, defaults):

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `false` | master switch |
| `ProfileKey` | `human-aesexpert-1b-expert-v1` | recorded on every run |
| `DefaultCapabilities` | `expert_scores` | requested when unspecified |
| `AllowExpertScores` | `true` | the only enabled capability |
| `AllowScoreHead` / `AllowMetaVoter` / `AllowTextAssessment` | `false` | prepared, off |
| `MaximumBatchItems` | `20` | max images per analysis request |
| `RequestTimeoutSeconds` | `120` | per-inference hard timeout |
| `SidecarBaseUrl` | (empty) | internal sidecar URL |
| `PreprocessingProfileKey` | `human-aesexpert-official-v1` | official 448 tiling |
| `MaxUploadBytes` | `26214400` | per direct upload |
| `Pepper` | (dev fallback) | owner-scoped container-key HMAC key |

The typed HTTP transport has no independent deadline: its timeout is explicitly
infinite so `RequestTimeoutSeconds` is the single authoritative bound. This is
intentional; the .NET `HttpClient` default of 100 seconds would otherwise abort
a valid request before the configured 120-second model deadline.

The web lab persists and displays every validated Expert-head metric. Select at
least two completed images and choose **Compare scores / Confronta punteggi** to
open a matrix with all 12 dimensions on a 0–10 display scale. The stored values
remain the original model `[0,1]` values; the UI-only multiplication does not
alter persisted data or model semantics.

## 4. Model readiness + controlled first run (smoke test)

Analyze ONE explicitly supplied local test image and print SAFE structural info
only (success, metric keys/count, duration, model/profile). **Never** use a
private production photo; **never** run a gallery backfill.

```bash
HUMANAES_URL=http://127.0.0.1:8091 \
./scripts/human-aesexpert-smoke.sh /path/to/your-test-image.jpg
```

(From the host, expose the port temporarily or run inside the internal network.)
Then, for a full end-to-end check through the real job path, add one lab item in
the UI and press **Avvia analisi / Start analysis** on a tiny batch.

### Measured real run (2026-07-14, one dev host — NOT production hardware)

A real controlled validation was executed on a disposable local stack. Do NOT
extrapolate these to production hardware.

- Host: Linux x86-64, Intel Core i7-8550U (4C/8T @ 1.8 GHz), 7.4 GiB RAM +
  15 GiB swap, CPU-only (no CUDA).
- Sidecar image: `nubarca-human-aesexpert:local` 1.69 GB (python:3.11-slim,
  `torch 2.4.1+cpu`, `transformers 4.44.2`, `accelerate 0.34.2`).
- **Direct inference** (one 512×512 synthetic image, official 448 tiling):
  model load **≈103 s**, inference **≈40 s** (reported `durationMs` 40415),
  peak RSS **≈4.8 GiB**, CPU **≈400 %** (4 cores). Response: 12 metrics, exact
  key order, **all values in [0,1]** (range 0.003–0.617), scale (0,1), no text,
  1745-byte body — **passed the strict validator**.
- **End-to-end durable job → PostgreSQL** (gated test
  `AestheticRealSidecarE2ETests`, disposable `pgvector/pgvector:pg17`): one item
  → one immutable run → one durable `BackgroundJob` (payload = run id only) →
  worker → real sidecar → **12 normalized metrics persisted in PostgreSQL**,
  model revision + official preprocessing + duration/timestamps persisted, raw
  output bounded, detail service exposes the 12 metrics (no text/score-head/
  meta-voter). **Micro-batch of 3**: 3 independent jobs, all succeeded (12
  metrics each), no duplicate live runs, sidecar stable at concurrency 1.

These gated tests are SKIPPED in normal CI; run them with `HUMANAES_E2E=1` plus
`HUMANAES_E2E_PG` / `HUMANAES_E2E_SIDECAR` pointing at a live disposable stack.

Operational note: on this low-end CPU host a single inference is ~40 s and peak
RSS ~4.8 GiB — usable for the opt-in, manual, bounded lab flow, but too slow for
any bulk/gallery use (which this feature deliberately forbids). Production
suitability must be re-measured on the target host.

## 5. Migration / deployment order

1. Back up the database.
2. Deploy code (build api/worker/frontend).
3. Apply the additive migration `AddAestheticsLab` via the standard prod migrate
   step (creates `aesthetic_lab_items`, `aesthetic_analysis_runs`,
   `aesthetic_metrics`, `aesthetic_text_results`, `aesthetic_lab_derivatives`).
   It does **not** auto-apply.
4. `up -d api frontend worker`. The feature stays disabled until step 3 above
   (Enabled + SidecarBaseUrl) — deploying the code alone changes nothing.
5. Install weights + start the sidecar (§1–2), then enable (§3).

## 6. Pause / disable / rollback / uninstall

- **Pause/disable:** set `HumanAesExpert__Enabled=false` (or stop the sidecar).
  Lab browsing still works; **Start analysis** returns a controlled unavailable
  response and creates no jobs. Existing results remain.
- **Rollback model:** point `HUMANAES_MODEL_DIR` at `<key>.prev` (or a previous
  dir) and restart the sidecar.
- **Uninstall without deleting results:** stop the sidecar, set `Enabled=false`,
  and (optionally) remove the weight directory. The database rows (items, runs,
  metrics) are unaffected. The feature/tables can be retired later with a
  separate additive-down migration if desired.

## Privacy / no-leak guarantees

- No cloud calls, no external telemetry, no model-hub calls at runtime; weights
  are local + read-only. The sidecar has no outbound network and no blob mount.
- No image bytes, filenames, metrics, or generated text are logged (sidecar or
  API); framework access logs are disabled on the sidecar.
- The job payload carries only the analysis-run id — never bytes, blob id, SHA,
  path, person names, prompts, or model output.
- `RawOutputJson` is bounded internal provenance; it is never returned by an
  ordinary API response and never replaces the normalized metrics.
- Owner-private only: no cross-owner exposure, no Gallery/Files/Party/TV/public
  surface, no ranking between people, and no health/medical terminology.

## Preprocessing profiles

- `human-aesexpert-official-v1` — the checkpoint's own 448×448 dynamic tiling
  (`use_thumbnail`, `max_num=12`, ImageNet normalization). The worker sends the
  immutable original bytes; the sidecar owns all model preprocessing, so a run
  is reproducible from (blob + profile). EXIF orientation is applied and
  metadata is otherwise ignored (pixels only).
- `human-aesexpert-controlled-v1` — a reduced single-tile profile for speed. It
  is **not** equivalent to the official pipeline and is recorded under its own
  key so a run is never mistaken for official behavior.

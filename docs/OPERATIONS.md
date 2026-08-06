# NubArca Operations

Day-to-day operation of a running NubArca deployment. For the first install
see [deploy/FIRST_DEPLOY.md](../deploy/FIRST_DEPLOY.md); for design and
invariants see [ARCHITECTURE.md](../ARCHITECTURE.md).

For an update of the current production host, use the mandatory
[fastdeploy runbook](../deploy/FAST_DEPLOY.md). Its four-file Compose stack and
OpenVINO image build supersede the generic examples below.

All commands assume the production stack and an operator shell on the host.
Adjust the compose invocation to your setup (you may also stack a local
override file, e.g. `-f docker-compose.prod.yml -f docker-compose.prod.local.yml`):

```bash
DC="docker compose -f docker-compose.prod.yml --env-file .env"
```

## Users

There is no public registration. Manage users with the operator CLI:

```bash
# Create or ensure a user (add --admin to grant the admin role).
$DC run --rm \
  -e NUBARCA_ADMIN_EMAIL=you@example.com \
  -e NUBARCA_ADMIN_DISPLAY_NAME="You" \
  -e NUBARCA_ADMIN_PASSWORD='<strong-password>' \
  api users ensure --admin

# Toggle the admin role on an existing user.
$DC run --rm api grant-admin --email you@example.com
$DC run --rm api revoke-admin --email someone@example.com
```

Admin changes take effect on the user's next request (no re-login needed).

## Database migrations

Migrations are additive and **do not** auto-apply at startup by default. Apply
them before bringing a new build up:

```bash
$DC run --rm api db migrate
```

## Cleanup (opt-in)

Two background services keep storage tidy. **Both are off by default** and must
be enabled (or run via the CLI). Until then, expired Trash and zero-reference
blobs accumulate.

- **File sweeper** — permanently purges files whose Trash grace window has
  passed. Enable with `FileItemSweeper__Enabled=true`
  (+ `FileItemSweeper__IntervalMinutes` / `__GraceMinutes`).
- **Blob janitor** — reclaims physical blobs whose reference count has reached
  zero after the final retained owner is removed and whose separate grace
  window has passed. Re-acquiring the blob cancels eligibility. Enable with
  `BlobJanitor__Enabled=true` (+ interval/grace). The two windows are
  sequential, so a 10-day Trash and 1-hour physical safety window use
  `FileItemSweeper__GraceMinutes=14400` and
  `BlobJanitor__GraceMinutes=60`.

> **Never delete blob files by hand.** A blob may be shared by several files
> via deduplication; only the janitor knows when bytes are safe to remove.
> Use `storage reconcile` (dry-run by default) to inspect on-disk orphans vs.
> missing rows.

## Background jobs (opt-in)

Backfill/maintenance work runs as durable DB-backed jobs. The in-process worker
is off by default (`Jobs__WorkerEnabled=false`); drive jobs out-of-band:

```bash
$DC run --rm api jobs list           # status counts + recent jobs
$DC run --rm api jobs run-once       # process one batch, then exit
$DC run --rm api jobs worker         # long-running loop (Ctrl-C to stop)
```

On a small server, prefer a separate low-priority run rather than the
in-process worker, e.g. `nice -n 15 ionice -c3 … jobs run-once`. Running more
than one worker only parallelises *distinct* jobs; a single import or backfill
job is processed by one worker.

## Admin server-side import (opt-in)

Imports a directory that already exists on the server into a user's library.
Enable with `AdminImport__Enabled=true` and configure one or more whitelisted
roots (`AdminImport__Roots__0`, `__1`, …), each mounted **read-only** into the
api container. Drive it from the admin UI (Import); queued imports run via the
jobs worker or `jobs run-once`.

Throttle on small hardware:

- `AdminImport__DelayBetweenFilesMs` — pause between files.
- `AdminImport__MaxBytesPerSecond` — cap per-file read rate (0 = unlimited).
- `AdminImport__MaxRunMinutes` — per-slice budget; the run pauses + re-queues
  and resumes safely (already-imported files are skipped).
- `AdminImport__YieldEveryFiles` — scheduler yield cadence.
- `AdminImport__DbBatchSize` — files persisted per database commit (default
  100; 1 = legacy per-file persistence). Batching removes the 3-4 fsyncs per
  file that dominated large imports; a failed batch automatically retries
  per-file, so correctness does not depend on it. The run's job log ends with
  a `db-batch …` line (counts + milliseconds only) showing batches, fallbacks,
  new/duplicate blobs, and where the remaining DB time goes.

Import only reads source files (never moves/deletes them), preserves the
directory structure as logical folders, and reuses dedup/quota/metadata/audit.

## Storage stats & integrity

The admin **Storage stats** page loads fast (aggregate counts + sizes) and
shows per-phase timing diagnostics. The physical blob-store scan
(physical/missing/unreferenced cross-check) is **on demand**: click **Run
integrity check**. A dominant "physical scan" time means the blob-store
filesystem walk, not PostgreSQL.

The integrity check counts a blob as present when its bytes exist in
**either** root (original or derived) — a displaced derivative is not data
loss. The same on-demand scan therefore also reports **derived readiness**:
how many derivative rows have their bytes in the derived root (where serving
reads them), only in the original root, or in neither. "Only in original
root" > 0 means the gallery silently regenerates those artifacts on first
view (CPU-heavy); fix it with the placement repair below.

## Derived artifact placement (verify / repair)

Derivative bytes (small/medium thumbnails, video posters) are served from the
**derived root** only. If `Storage__DerivedRootPath` is introduced or changed
after artifacts were generated, the rows stay consistent and the integrity
check stays clean, but the bytes sit in the original root and every first
view pays an ImageSharp regeneration. Slow gallery scrolling plus an API CPU
spike while thumbnails load is the classic symptom (the lazy endpoints also
log `Derived artifact missing from derived root; ... action=...` and
self-repair displaced bytes with a plain copy on first request).

**A. Confirm configuration** (in `.env` / compose):

- `Storage__RootPath` and `Storage__DerivedRootPath` are what you expect;
- the original-blobs volume is mounted at `Storage__RootPath` and the derived
  volume at `Storage__DerivedRootPath`;
- **api and worker mount both volumes at the same container paths** (a worker
  without the derived mount writes derivatives into its own container layer).

**B. Audit placement** (read-only, counts only):

```bash
$DC exec api dotnet NubArca.Api.dll media derivatives verify-bytes
# checked / present_in_derived_root / only_in_original_root /
# missing_from_both, plus per-size lines (small / medium / poster).
# --size small|medium|poster and --limit N narrow the walk.
```

**C. If `only_in_original_root` > 0 — repair placement:**

```bash
$DC exec api dotnet NubArca.Api.dll media derivatives repair-bytes --dry-run
$DC exec api dotnet NubArca.Api.dll media derivatives repair-bytes
$DC exec api dotnet NubArca.Api.dll media derivatives verify-bytes
```

The repair is a streaming copy (re-hash + temp file + atomic rename): **no
image decode, no DB writes, original bytes never deleted**. After it,
`only_in_original_root` must be 0. Rows whose bytes are missing from **both**
roots are left unchanged unless you explicitly pass `--regenerate-missing`
(CPU-heavy standard regeneration).

**D. After a clean import/backfill on split roots**, `verify-bytes` should
report everything `present_in_derived_root`, with `only_in_original_root=0`
and `missing_from_both=0` (barring known failed derivatives).

**E. Gallery validation**: open the gallery and scroll — existing thumbnails
must not produce repeated `Derived artifact missing from derived root` logs
or CPU-heavy regeneration; the .NET container CPU should stay flat.

## Why are derivatives missing? (failure diagnostics)

`verify-bytes` above checks the *bytes of existing* derivative rows. To explain
derivatives that have **no** row at all — the "Images missing small/medium" /
"Videos missing poster" counts on the Admin Stats page — use the durable
diagnostics. After a backfill has attempted the missing files:

```sh
$DC exec api dotnet NubArca.Api.dll media derivatives failures
```

It prints aggregate reasons by size / status / error code / detected format
(e.g. `18 image/tiff unsupported_format`, `67 decode_failed`) — counts only,
never names/paths. Permanent failures are skipped by default; re-attempt them
with `media derivatives backfill --retry-failed`. Full reference:
[`docs/media-derivatives.md`](media-derivatives.md).

## Derivative backend (libvips / ImageSharp)

Thumbnails/previews render via libvips (bundled, no apt packages) with automatic
fallback to ImageSharp. libvips is ~2–3× faster on typical photos (shrink-on-
load). It is the default (`MediaDerivatives__ImageBackend=auto`); force the
managed path with `=imagesharp`. Compare them on your data:

```sh
$DC exec api dotnet NubArca.Api.dll media derivatives benchmark --limit 50
```

If the native library can't load, startup logs `libvips backend unavailable`
and everything runs on ImageSharp — no action needed. Backend usage + fallback
counts appear in `media.derivatives.backfill` logs; per-file failures that fell
back show in `media derivatives failures`. Full reference:
[`docs/media-derivatives.md`](media-derivatives.md#image-backends-libvips--imagesharp).

## Blob reference integrity (audit / repair)

`BlobObject.ReferenceCount` is derived accounting. The owners include active
`file_items`, `file_thumbnails`, `face_previews`, Plates/Party derived rows and
Aesthetics Lab items/derivatives. Some owners, notably `face_previews`, use a
plain correlation id rather than a database foreign key and must still be
counted. A hard interruption (worker kill, crash) between the refcount increment
and the owner-row commit leaks one reference: the blob has a nonzero refcount,
no owners, and the janitor — which only reclaims zero-reference blobs — can never
clean it. The on-demand admin integrity check reports this drift (refcount
mismatches / leaked refs / zero-ref-with-owners); the same numbers come from the
CLI:

```bash
$DC exec api dotnet NubArca.Api.dll storage blobs audit-references
$DC exec api dotnet NubArca.Api.dll storage blobs repair-references --dry-run
$DC exec api dotnet NubArca.Api.dll storage blobs repair-references
```

Repair recomputes each mismatched count from the owner tables (guarded
against concurrent changes) and **never deletes physical bytes** — corrected
zero-reference blobs are reclaimed by the blob janitor under its normal grace
rules. Prefer running it during quiet periods; rows that change concurrently
are skipped and picked up by the next run.

## Staging sessions stuck "importing"

A permanently-failed staging import marks its session `failed`
(`import_failed`), so it can be discarded from the UI and is reclaimed by the
cleanup sweeper after expiry. If a session still shows `importing` (legacy
stuck data, or run/job rows removed), the DELETE endpoint now inspects the
linked import run/job: a live (queued/running/paused) import still refuses the
delete — cancel it first — while a terminal or missing run/job allows a safe
discard.

The worker logs `staging_configured=` / `derived_root_split=` flags at
startup (`jobs worker`): if `staging_configured=false` despite a correct
environment, the container is running an image whose CLI host predates the
StagingOptions binding fix — update it.

## Backup & restore

```bash
# Cold backup (stops api/frontend briefly): pg_dump + tarred storage volume.
./deploy/backup.sh

# Restore — dry-run first (validates checksums, shows the plan), then --yes.
./deploy/restore.sh <backup-dir>
./deploy/restore.sh <backup-dir> --yes      # DESTRUCTIVE: replaces volumes
```

If a separate derived-media root (`Storage__DerivedRootPath`) is configured, a
**full** backup must include it; otherwise derived artifacts regenerate on
demand after a restore. See [deploy/SMOKE_CHECKLIST.md](../deploy/SMOKE_CHECKLIST.md)
for the post-restore drill.

## Upload limits

Keep the three limits consistent or uploads fail confusingly:

- App: `Storage__MaxUploadBytes` (per-file cap; 0 = unlimited) and
  `Storage__DefaultUserQuotaBytes` (per-user logical quota).
- Kestrel/form: `Uploads__MaxRequestBodySizeBytes` / `Uploads__MaxFileSizeBytes`.
- Reverse proxy: the body-size limit (e.g. nginx `client_max_body_size`).

## PostgreSQL maintenance

Autovacuum handles routine maintenance. After a large import, a one-off
`ANALYZE;` refreshes planner statistics. Inspect live activity with
`SELECT pid, state, wait_event_type, query FROM pg_stat_activity WHERE state <> 'idle';`
(run inside the DB shell only — never copy query text elsewhere). `REINDEX`
only if an index is visibly bloated after heavy churn. See
[deploy/FIRST_DEPLOY.md](../deploy/FIRST_DEPLOY.md) for the detailed
maintenance section.

## AI runtime placement (OpenVINO)

AI inference runs in-process in the api and worker containers. The execution
provider and per-model device are configuration, and invalid configuration is
rejected **at startup** by `AiOnnxOptionsValidator` rather than silently
degrading to CPU:

```jsonc
{
  "Ai": {
    "Onnx": {
      "ModelDir": "/models/ai",
      "ExecutionProvider": "openvino-direct",
      "OpenVino": {
        "NativeDir": "/opt/nubarca/ort-openvino",  // baked into the runtime-openvino image
        "CacheDir": "/tmp/ov-cache",               // bounded writable compile cache
        "FaceDetectorDevice": "GPU",               // CPU | GPU, independent per model
        "FaceRecognizerDevice": "CPU",
        "GpuPrecision": "FP32"                     // required for equivalence with CPU
      }
    }
  }
}
```

Environment form: `Ai__Onnx__ExecutionProvider`,
`Ai__Onnx__OpenVino__FaceDetectorDevice`, and so on. Valid direct devices are
**`CPU`** and **`GPU`** only; `DUAL` / `AUTO` / `MULTI` / `HETERO` are rejected.

Confirm what is actually running rather than what is configured:

```bash
# provider + device + native/OpenVINO versions + ABI match + loaded providers
$DC exec api dotnet NubArca.Api.dll ai onnx runtime-info
# → configuredProvider=openvino-direct, providers=[…,OpenVINOExecutionProvider], abiMatch=True

# resource use while a backfill runs
docker stats --no-stream nubarca-api nubarca-worker
$DC exec api sh -c 'grep VmRSS /proc/1/status'
intel_gpu_top
```

## Troubleshooting

- **Logs** (sanitized — no secrets/paths/keys):
  `$DC logs api --tail=100 -f`, `$DC logs postgres`, `$DC logs frontend`.
- **Health**: the API exposes `/health` (point your uptime monitor there via
  the reverse proxy).
- **Admin page empty / 403**: confirm the user is admin (`grant-admin`) and has
  logged in since; the backend gates `/api/admin/*` independently of the UI.
- **Do not** expose PostgreSQL publicly, commit `.env`, or delete blob files
  manually.

# NubArca — agent context

Stable rules and operational facts for Claude/agent sessions. Read this and
[docs/current-work.md](docs/current-work.md) before starting any task.
Transient/branch status lives in `docs/current-work.md`, **not** here.

Stack: C# / ASP.NET Core (minimal APIs) + PostgreSQL + EF Core; React + TypeScript
frontend; a .NET worker for background jobs; local content-addressed blob storage.

## NubArca project invariants

- Original storage is content-addressed SHA-256 **immutable** blobs.
- `FileItem` is the logical, user-visible file/path.
- `BlobObject` is the physical, content-addressed storage object.
- Physical layout: `/storage/objects/{first2}/{next2}/{sha256}`. Never store
  binaries in PostgreSQL; never use the user-visible path as the storage path.
- Move/rename is **DB-only** and must not rewrite original blobs.
- SHA-256 is used for exact dedup; if a blob exists, do not duplicate content.
- `BlobMetadata` is global/blob-level.
- `FileItemUserMetadata` is owner/user-scoped.
- `FileThumbnail` rows represent successful derived artifacts.
- Derived artifacts are cache/regenerable.
- Grid/list should use **small** thumbnails.
- Viewer/lightbox should use **medium** preview.
- Original full-res must only be served through explicit content/download endpoints.
- Video cards use the **poster**; video playback uses the **video** endpoint.

## Security / privacy / no-leak rules

Never expose through API, DTOs, logs, diagnostics, public shares, or CLI unless
explicitly designed and reviewed as safe:

- `StorageKey`, physical paths
- raw metadata JSON, `PayloadJson`
- `TokenHash`, share tokens
- `BlobId`, SHA / content hash
- stack traces, password hashes, secrets
- raw AI vectors, raw model payloads

GPS / DateTaken and AI-derived data are allowed in **owner-private** flows, but
never in public shares or unsafe aggregates unless explicitly designed.

Also: validate all input; prevent path traversal; centralized authorization on
every download; HTTP-only cookies; rate-limit auth + public share endpoints;
audit upload/download/delete/share create/revoke.

## AI product rule

The owner/holder of a file owns **all** data derived from that file: EXIF, GPS,
DateTaken, OCR/extracted text, AI captions/descriptions, visual embeddings,
semantic embeddings, face detections, face embeddings, face clusters, AI tags,
similarity relationships, and future AI metadata.

Owner-private APIs may expose rich derived data to the owner. Privacy boundary:

- no leakage to other users
- no leakage to public shares unless explicitly designed
- no leakage through logs / diagnostics / unsafe DTOs
- no cross-owner AI search
- no global face/person clustering across owners
- no raw vectors exposed through API/CLI

## AI substrate rules

- AI is **disabled by default** unless explicitly enabled.
- Provider selection is **profile-driven** via `AiModel.Provider` + `AiProfile`.
- AI outputs are keyed by `ProfileId`.
- A missing `BlobAiArtifactStatus` row means **implicit pending**.
- Do **not** materialize pending rows for every `BlobObject × Profile`.
- `skipped` means a content-related **permanent** skip only.
- Provider unavailable is an environment/config state, **not** a content failure:
  it must not mark blobs/files `skipped` or `failed`.
- The deterministic backend is **dev/test only** and not semantically meaningful.
- No pgvector until the explicit pgvector phase.
- No ONNX / external provider unless the current task explicitly asks for it.

## Job rules

- Jobs are priority-aware, sliceable, checkpointed, cancellable, retry-aware,
  and cooperative.
- Foreground imports must not wait behind long maintenance work beyond a bounded
  slice.
- Cancellation must not record a permanent failure.
- Backfills use keyset paging where applicable; do **not** load all candidates
  into memory.

## Quality & scope

- Prefer simple, explicit, boring code; no abstractions before they are needed.
- Every implemented feature has tests; run build + tests after each task.
- Implement only the requested scope. Out of scope unless explicitly requested:
  WebDAV, plugin system, calendar, contacts, chat, collaborative editing,
  desktop/mobile sync, HLS/DASH transcoding, advanced permissions, public
  registration.

## Production deployment rules

- **Mandatory before every deploy:** read
  [deploy/FAST_DEPLOY.md](deploy/FAST_DEPLOY.md) immediately before running any
  production command. It is the source of truth for the current four-file
  Compose stack, immutable image builds, OpenVINO target, smoke checks and
  rollback. Do not deploy from remembered chat commands.
- **Before deploying to an existing installation, obtain the production checkout
  location and connection settings from the operator. Never infer or hardcode a
  host path.** A host, login, directory and public origin belong to one
  installation, not to NubArca, so they are never written into tracked source.
- Required operator configuration, validated by
  [scripts/lib/operator-config.sh](scripts/lib/operator-config.sh):
  `NUBARCA_PRODUCTION_SSH`, `NUBARCA_PRODUCTION_CHECKOUT`, and where relevant
  `NUBARCA_PUBLIC_ORIGIN`, `NUBARCA_STORAGE_ROOT`, `NUBARCA_SERVICE_ROOT`,
  `NUBARCA_IMPORT_ROOT`, `NUBARCA_TV_APK_DIR`,
  `NUBARCA_ENCRYPTED_BACKUP_TARGET`. Missing values fail closed.
- Use them generically:

  ```bash
  ssh "$NUBARCA_PRODUCTION_SSH"
  cd "$NUBARCA_PRODUCTION_CHECKOUT"
  ```

- Public origin: read `NUBARCA_PUBLIC_ORIGIN` from the production `.env`
  (`grep '^NUBARCA_PUBLIC_ORIGIN=' .env`), never by sourcing the file.

The current release deployment always uses all four Compose files:

```bash
DC="docker compose -f docker-compose.prod.yml -f docker-compose.prod.local.yml -f docker-compose.facedirect-api.yml -f docker-compose.release.local.yml --env-file .env"
```

`scripts/prod-dc.sh` is a base-stack helper, not a release-deploy helper. It
does not include the OpenVINO and immutable release-image overrides.

**NEVER `source` the prod `.env` before `docker compose`** (no `set -a; . ./.env`,
no `source .env`). `ConnectionStrings__Postgres` contains `;` (e.g.
`Host=postgres;Port=5432;…;Password=…`); a POSIX shell treats `;` as a command
separator, so sourcing truncates the value to `ConnectionStrings__Postgres=Host=postgres`
in the shell environment. Compose gives shell env vars **precedence over
`--env-file`**, so the recreated api/worker then start with a **passwordless**
connection string and crash-loop (`Npgsql … No password has been provided …
SASL/SCRAM-SHA-256`, failing in `ApplyStartupMigrationsAsync` on boot). Always let
`--env-file .env` supply the values; if you must read a single var, use
`grep '^KEY=' .env` (not `source`). To recover from a poisoned recreate: in a
fresh shell (or after `unset ConnectionStrings__Postgres POSTGRES_PASSWORD`),
`$DC up -d --force-recreate api worker frontend` and confirm the container env with
`docker inspect nubarca-api --format '{{range .Config.Env}}{{println .}}{{end}}' | grep ConnectionStrings`
shows the FULL string (incl. `Password=`).

## Product identity

The product is **NubArca**. `scripts/check-nubarca-identity.sh` asserts a
**positive identity contract** over tracked source, and `NubArcaIdentityTests`
runs it inside `dotnet test`.

The contract states what the product *is* — `NubArca.sln`, the `NubArca.Api`
assembly and namespaces, `NubArca.Api.Tests`, the `nubarca-frontend` package,
the `it.littlefly.nubarca.tv` TV package, the `NubArca.` cookie prefix,
`nubarca`-named Compose resources, one agreed release version — and fails when
any of that stops being true. It carries no denylist: asserting the current
truth catches a drift to *any* other spelling, and keeps the repository from
having to remember what it used to be called.

Its second half is that source describes the **product**, never one
installation. IP literals, `login@host` targets, this installation's public
hostname, a `NUBARCA_*` variable that falls back to a path or URL, and `cd` into
a host checkout directory are all contract failures. Run
`scripts/check-nubarca-identity.sh --self-test` to see the exact boundaries.

## Validation commands

Identity:

```bash
scripts/check-nubarca-identity.sh
scripts/check-nubarca-identity.sh --verbose     # list every assertion
scripts/check-nubarca-identity.sh --self-test   # prove the detectors work
```

Backend:

```bash
scripts/test-backend-fast.sh
# Release/full coverage (external dependencies may be skipped if unavailable):
scripts/test-backend-full.sh
```

Frontend (there is no ESLint in this repo; `lint` is the TypeScript check, and
delegates to `typecheck`):

```bash
cd frontend
npm run lint       # === npm run typecheck === tsc -b --noEmit --force
npm run build
npm run test:run   # `npm test` is vitest in watch mode
cd ..
```

`typecheck` passes `--force` so the result never depends on incremental
`.tsbuildinfo` state.

**Never pipe a validation command into `head`/`tail` without `set -o pipefail`.**
A pipeline reports the LAST command's status, so `npm run lint 2>&1 | tail -5`
exits 0 even when the type check failed — this has already produced one false
green. Run it bare, or set `pipefail` first.

Production validation:

```bash
$DC exec api dotnet NubArca.Api.dll storage blobs audit-references
$DC exec api dotnet NubArca.Api.dll media derivatives verify-bytes
$DC exec api dotnet NubArca.Api.dll ai status
$DC ps
```

## Agent workflow

- Read `CLAUDE.md` and `docs/current-work.md` before starting.
- Keep scope narrow; do not expand scope without saying so.
- Prefer small branches and reviewable commits.
- Run the relevant tests before committing.
- Final summary must include: branch, commit hash, tests run, failures/deviations,
  and the next recommended step.
- Update `docs/current-work.md` after each major slice.

## graphify

This project has a knowledge graph at graphify-out/ with god nodes, community structure, and cross-file relationships.

Rules:
- For codebase questions, first run `graphify query "<question>"` when graphify-out/graph.json exists. Use `graphify path "<A>" "<B>"` for relationships and `graphify explain "<concept>"` for focused concepts. These return a scoped subgraph, usually much smaller than GRAPH_REPORT.md or raw grep output.
- If graphify-out/wiki/index.md exists, use it for broad navigation instead of raw source browsing.
- Read graphify-out/GRAPH_REPORT.md only for broad architecture review or when query/path/explain do not surface enough context.
- After modifying code, run `graphify update .` to keep the graph current (AST-only, no API cost).

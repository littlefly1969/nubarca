# NubArca production fastdeploy

> **Mandatory agent gate:** read this file immediately before every production
> deploy. Do not rely on commands remembered from an earlier chat.

## Operator configuration

Before deploying to an existing installation, obtain the production checkout
location and connection settings from the operator. Never infer or hardcode a
host path.

This runbook is written against operator-supplied configuration, because a host,
a login and a directory belong to one installation and not to NubArca:

| variable | meaning |
| --- | --- |
| `NUBARCA_PRODUCTION_SSH` | ssh destination of the installation, e.g. `user@host` |
| `NUBARCA_PRODUCTION_CHECKOUT` | absolute path of the deployment checkout on that host |
| `NUBARCA_PUBLIC_ORIGIN` | externally reachable HTTPS origin, read from the production `.env` with `grep '^NUBARCA_PUBLIC_ORIGIN=' .env` — never `source` the file |

`scripts/lib/operator-config.sh` validates these and fails closed when one is
missing, so a script can never quietly act on the wrong machine. Export them, or
pass them inline, before running anything below:

```bash
export NUBARCA_PRODUCTION_SSH="…"        # from the operator
export NUBARCA_PRODUCTION_CHECKOUT="…"   # from the operator
ssh "$NUBARCA_PRODUCTION_SSH"
cd "$NUBARCA_PRODUCTION_CHECKOUT"
```

## Invariants

Production always stacks these files, in this order:

```bash
docker compose \
  -f docker-compose.prod.yml \
  -f docker-compose.prod.local.yml \
  -f docker-compose.facedirect-api.yml \
  -f docker-compose.release.local.yml \
  --env-file .env
```

`scripts/prod-dc.sh` represents only the base production stack. Do not use it
for a release deployment because it intentionally lacks the OpenVINO and
immutable-image overrides.

## TV release boundary

[`../docs/tv-release.md`](../docs/tv-release.md) is the only authorized APK/OTA
procedure. An ordinary compatible JavaScript OTA does not rebuild an APK or
container, run a database migration, or restart the API. When the OTA depends
on a new backward-compatible backend API, deploy and verify that backend first,
then use the TV runbook. A native TV release also uses that runbook; do not add a
second APK/OTA command sequence here.

An OTA is Git-first. GitHub Environment `tv-production` is the only ordinary
signer and publishes an immutable GHCR bundle; GitHub never connects to
production. The server only verifies and imports that bundle by digest. Its
production `.env` must carry `NUBARCA_TV_OTA_STORAGE_ROOT`,
`NUBARCA_TV_OTA_CERTIFICATE` and `NUBARCA_TV_NODE`, all non-secret local paths.
The OTA private key must not be present on the server or in `.env`. See
[`../docs/tv-release.md`](../docs/tv-release.md) §§3–6.

Never source `.env`. Let Compose read it through `--env-file .env`; sourcing it
can truncate the semicolon-delimited PostgreSQL connection string.

Do not use `--remove-orphans`. The HumanAesExpert and direct-import containers
may legitimately be managed by separate Compose invocations.

## Guided two-command path

For an ordinary release with CI images, including an approved additive database
migration, the server provides a review/apply pair:

```bash
./deploy/update-production.sh check --env-file .env
./deploy/update-production.sh apply --env-file .env --confirm <full-main-sha> [--confirm-migrations]
```

`check` fetches `origin/main` and reports, without changing the checkout,
release pins or containers, which backend/frontend/TV components changed and
whether the corresponding immutable GHCR artifacts exist. A TV change requires
either a native APK bundle or a signed OTA bundle for the exact SHA; an absent
TV artifact is never silently skipped. It prints the exact
`apply` command. For an approved migration it also verifies the configured
backup target and database-size capacity, lists the exact migration ids and adds
`--confirm-migrations` to that command. Read the report before continuing; copy
the command it prints rather than reconstructing it.

`apply` requires the same full SHA, refuses if `origin/main` moved, refuses a
dirty/non-`main` checkout, applies the root-capacity gate, pulls images by
digest after fast-forwarding to the confirmed commit, runs the image verifiers
and the effective-Compose gate, then recreates only affected application services with
`--no-build`. For an approved migration, it pulls and verifies the candidate API
image first, creates and validates a pre-migration PostgreSQL dump on the
dedicated backup mount, applies the migration with that candidate image, and
verifies every expected id in `__EFMigrationsHistory` before changing a release
pin. It restores the previous pin if smoke checks fail; this rollback is allowed
only because the tracked migration policy has declared the old application
compatible with the new schema. A TV bundle is activated locally only when CI
published one for that exact source SHA.

The guided path deliberately refuses a rewritten, destructive, unclassified or
previous-application-incompatible migration. Those are exceptional releases and
use the manual review path in §4.3. The script also never cleans Docker or
storage. Installing this helper—or upgrading an older helper that still refuses
all migrations—necessarily needs one manual `git pull --ff-only origin main`;
subsequent runs fetch and fast-forward themselves.

## 1. Establish the exact release

Before changing the server:

```bash
git status --short
git branch --show-current
git rev-parse HEAD
git rev-parse origin/main
git log -5 --oneline
```

Requirements:

- local working tree is clean;
- branch is `main`;
- `HEAD` equals `origin/main`;
- identify changed runtime services from the diff;
- identify whether the release contains an EF migration.

Record disk usage before building. This check is read-only — nothing below is
ever run automatically:

```bash
df -h / /var/lib/docker
docker system df
docker builder du
```

The root filesystem is the authoritative capacity gate. Docker's configured
data root may be a separate filesystem while the active `containerd` content
store and build snapshots still consume `/`. If `/` is at or above 90% usage,
or has less than 10 GiB available, **stop the deploy here**. Reclaiming disk
space is always a separate, explicit operator decision made outside this
runbook — never an automatic step of a deploy. Never try to recover space by
deleting files directly below `/var/lib/containerd` or `/var/lib/docker`.

On the server, inspect the current checkout, release pins, container state and
active jobs before pulling. Never overwrite a dirty server checkout.

```bash
cd "$NUBARCA_PRODUCTION_CHECKOUT"
git status --short
git rev-parse HEAD
docker compose \
  -f docker-compose.prod.yml \
  -f docker-compose.prod.local.yml \
  -f docker-compose.facedirect-api.yml \
  -f docker-compose.release.local.yml \
  --env-file .env ps
git pull --ff-only origin main
```

Use the first 12 characters of the full commit SHA as `<shortsha>`.

## 2. Obtain the images (the server does not build them)

**No application image is built here.** CI builds api, worker and frontend,
verifies each one and publishes them to GHCR under the full source SHA (§2.1).
The production Compose model carries no `build:` recipe for ANY application
service, so a build cannot happen from this stack even by accident — `docker
compose build api` fails with "neither an image nor a build context", and
`up --build` has nothing to compile. That is the intended answer, not a
limitation to work around.

The API and worker both run the OpenVINO target, so both take the SAME image.
The frontend is its own image.

```bash
IMAGE_DIGEST='ghcr.io/<owner>/nubarca-api-openvino@sha256:<digest>'
docker pull "$IMAGE_DIGEST"
```

Pull by **digest**, not by tag. The `:<full-git-sha>` tag is the readable name
and is what a human quotes; the digest is what fixes the bytes. A tag can be
moved, a digest cannot, and the thing production runs should be the one that
cannot change under it.

If the GHCR package is public no credential is needed. If it is private, log in
with a **read-only** `read:packages` credential. A publishing token never
belongs on the production host.

```bash
FRONTEND_DIGEST='ghcr.io/<owner>/nubarca-frontend@sha256:<digest>'
docker pull "$FRONTEND_DIGEST"
```

Documentation/test-only commits do not require a new image at all.

## 2.1 Production image build (GitHub)

The `Build production images` workflow builds the SAME two API targets on a
GitHub runner and publishes them to GHCR. It is `workflow_dispatch` only.

**This is the deploy path for every application image.** `docker-compose.prod.yml`
carries no `build:` recipe at all, so what CI publishes here is what production
runs; §2 pulls it and §3 gates it.

Backend and frontend build as INDEPENDENT parallel jobs. They share only the
source SHA, so a frontend failure never withholds a good backend image, and
neither waits on the other.

What one run records:

| | |
| --- | --- |
| Source SHA | `github.sha` of the dispatched ref, stamped into both images |
| Lean runtime | `ghcr.io/<owner>/nubarca-api:<full-git-sha>` |
| OpenVINO runtime | `ghcr.io/<owner>/nubarca-api-openvino:<full-git-sha>` |
| Frontend | `ghcr.io/<owner>/nubarca-frontend:<full-git-sha>` |
| Digest | printed per image in the run summary |

Only the immutable full-SHA tag is published. There is deliberately no `latest`:
a deploy must be able to name the exact commit that produced the bytes it runs,
and a moving tag cannot answer that.

All application images are built into the runner's daemon and **verified before they are
pushed**, by the same script available locally:

```bash
scripts/verify-production-image.sh <image-ref> <expected-git-sha> [runtime|openvino]
```

It checks provenance (`NUBARCA_GIT_SHA` equals the source SHA — the lean
`runtime` target stamps this too, not only `runtime-openvino`), a startable
ASP.NET Core runtime with the published application, `ffmpeg`/`ffprobe`, and the
ONNX Runtime layer each variant is SUPPOSED to carry: the CPU provider beside
the application for `runtime`, and for `runtime-openvino` the staged
`libonnxruntime.so.<abi>` under its SONAME, the OpenVINO providers, the CPU and
GPU plugins and the Intel OpenCL userspace. The expected ABI is read from
`scripts/openvino-direct/onnxruntime-openvino.lock`, so it cannot drift into
asserting a version nobody ships.

The lean variant is also asserted NOT to carry the OpenVINO directory. Being
lean is what that target is for; an image that quietly grew the GPU layer is a
different image from the one §2 describes.

**GPU execution is not tested here and must not be.** `/dev/dri`, the render
group and the model mounts belong to an installation, never to a build host or
a CI runner. What the workflow can prove is that the GPU variant CONTAINS the
native layer and Intel userspace a GPU device would need; that the device works
is established on the installation, unchanged, by §6.

## 3. Gate images before changing release pins

Do not edit `docker-compose.release.local.yml` until the image has passed both
gates below.

**3a. Verify the pulled image itself.** This is a SECOND, independent check —
CI already verified the image before publishing it, and this one runs against
the bytes that actually arrived on this host:

```bash
scripts/verify-production-image.sh \
  'ghcr.io/<owner>/nubarca-api-openvino@sha256:<digest>' \
  <full-source-sha> \
  openvino
```

It must print `IMAGE VERIFIED` and a `NUBARCA_GIT_SHA` equal to the source SHA.
Anything else stops the deploy here.

**3b. Verify the EFFECTIVE Compose model**, after pinning the digest and before
recreating anything. What matters is not what the files say separately but what
the four of them resolve to together:

```bash
docker compose \
  -f docker-compose.prod.yml \
  -f docker-compose.prod.local.yml \
  -f docker-compose.facedirect-api.yml \
  -f docker-compose.release.local.yml \
  --env-file .env \
  --profile worker \
  config
```

Confirm in that output:

- `api.image` and `worker.image` are both the pinned `@sha256:` digest — the
  SAME one, because both run the OpenVINO target;
- `api.build` and `worker.build` are **absent**;
- the GPU wiring survives: `/dev/dri` on both, `group_add` carrying
  `OPENVINO_RENDER_GID`, and the device placements unchanged (API: detector GPU,
  recognizer CPU, photoText CPU; worker: detector GPU, recognizer GPU,
  photoImage CPU).

`--profile worker` is needed or `worker` does not appear in the output at all,
and its absence reads like a missing service rather than a hidden one.

For a frontend image, the equivalent of 3a is its own verifier, which runs the
container rather than only listing files — a `dist/` that copied cleanly and an
nginx that answers correctly are not the same statement:

```bash
scripts/verify-production-frontend-image.sh \
  'ghcr.io/<owner>/nubarca-frontend@sha256:<digest>' \
  <full-source-sha>
```

It must print `FRONTEND IMAGE VERIFIED`. It checks the OCI revision label, that
`nginx -t` accepts the shipped configuration, content-hashed Vite bundles that
`index.html` actually references, the absence of node/npm in the runtime layer,
and the two halves of the nginx contract that matter: a client-side route falls
back to `index.html` with 200, while a MISSING `/assets` file still answers 404.
That second half is the one worth having — a stale client that receives HTML
where it expected JavaScript fails later, somewhere else, as a parse error.

It deliberately does not test `/tv.apk` or `/download/tv/*`. Those come from an
installation volume, never from the image — the same separation as `/dev/dri`
for the backend. §6 checks them after the deploy, where they exist.

For 3b, `frontend.build` must be absent alongside `api.build` and
`worker.build`, and the frontend must keep its published port, its APK volume
and the internal network.

If a gate fails, leave the current release pins and running containers
untouched.

## 4. Database migration, when present

### 4.1 Automation policy

`deploy/migration-policy.json` is the reviewed production contract. An EF
migration may use the guided path only when all of these are true:

- the diff adds a new migration implementation and its Designer file, and
  modifies the model snapshot;
- no existing migration is modified, renamed or deleted;
- the migration id is declared `automated: true`;
- `previousApplicationCompatible: true` states that the previously pinned
  application can still run against the upgraded schema;
- the policy carries a non-empty compatibility reason.

`deploy/production-migration-plan.py` validates those facts from Git objects at
the confirmed candidate SHA. The policy is read from the candidate commit, not
from an uncommitted working tree. A missing or false declaration stops `check`;
the script never tries to infer from SQL whether a migration is safe.

The previous-application compatibility declaration is what permits the normal
smoke-failure rollback to restore the old image pins after the schema changed.
Without it, an image rollback could be more destructive than the failed deploy.

### 4.2 Guided backup and migration

If `check` reports approved migration ids, its generated command includes
`--confirm-migrations`. `apply` then performs this order:

1. take the deployment lock and re-confirm the immutable source SHA;
2. fast-forward the clean checkout to that confirmed commit;
3. pull and verify the candidate API/frontend images;
4. verify the candidate effective Compose model without changing pins;
5. create a PostgreSQL dump below the absolute `BACKUP_DIR` from `.env`;
6. verify gzip integrity, the dump completion marker, the presence of
   `__EFMigrationsHistory`, and record a SHA-256 sidecar;
7. run `db migrate` with the candidate API image on PostgreSQL's real Compose
   network, passing only the `db migrate` arguments to its existing entrypoint;
8. verify every planned id in `__EFMigrationsHistory`;
9. record the backup and migration ids in the ignored, server-local
   `deploy-history/`, then update pins and containers.

The backup filesystem must already exist and be writable. The capacity gate is
conservative: free space must cover 120% of the uncompressed database size plus
1 GiB. Nothing sources `.env`, no database password is printed, and neither a
failed dump nor a failed migration changes release pins or recreates a service.
The verified backup remains in place on every later failure.

Do not pass `--confirm-migrations` to a no-migration release. Both omitting it
when required and supplying it when not required fail closed and direct the
operator back to `check`.

### 4.3 Manual exception path

Use this only when `check` refuses the migration policy and the migration has
received a separate, explicit restore/compatibility review. Do not weaken the
policy merely to make the automatic command proceed. The manual sequence is:

1. create and verify the normal production backup;
2. run `db migrate` with the newly built API image and the complete four-file
   Compose stack;
3. stop if migration fails; do not recreate application containers.

Do not run a migration for releases without migration files.

#### 4.3.1 Where backups go

**Backups do not live on the root filesystem.** `BACKUP_DIR` in the production
`.env` points at a dedicated large data mount, and the checkout's `backups/`
directory is a symlink to it. Read the target and its free space rather than
assuming either:

```bash
grep '^BACKUP_DIR=' .env
df -h "$(grep '^BACKUP_DIR=' .env | cut -d= -f2-)"
```

This matters because §1's capacity gate is about `/`, where the image build
happens — it is **not** a reason to skip or shrink a backup. A full backup is
sized against the backup mount, which is provisioned for it. Do not reason from
`df -h /` to "there is no room for a backup"; that conclusion is wrong here and
would trade the one artifact that makes a migration reversible for nothing.

Verify a dump before relying on it — a truncated dump is worse than none,
because it looks like a backup:

```bash
gzip -t <dump>.sql.gz                       # integrity
zcat <dump>.sql.gz | tail -2                # terminated cleanly
zcat <dump>.sql.gz | sed -n '/CREATE TABLE public.users/,/);/p'
```

The last line is the one that proves the dump captured the PRE-migration
schema, which is what a rollback needs. Name it after the slice it precedes
(`pre-<slice>-<UTC stamp>.sql.gz`), matching the existing files in that
directory.

#### 4.3.2 Running `db migrate` off the newly built image

The migration must run on the image being released, not on the currently pinned
one, and the pins are not updated until §5. Run the new image directly on the
Compose network:

```bash
docker run --rm --network "$(docker inspect nubarca-postgres \
  -f '{{range $k,$v := .NetworkSettings.Networks}}{{println $k}}{{end}}' | head -1)" \
  --env-file .env \
  nubarca-api:release-<shortsha> db migrate
```

Pass **only the verb**. The image's `ENTRYPOINT` is already
`["dotnet", "NubArca.Api.dll"]`, so anything after the tag is appended as
arguments to the running application. Writing
`… nubarca-api:release-<shortsha> dotnet NubArca.Api.dll db migrate` hands the
CLI four arguments whose first is `dotnet`; no subcommand matches, and the
process falls through to **starting a full API host** — which then applies
pending migrations itself through `Database:MigrateOnStartup` and, far worse,
starts BlobJanitor, FileItemSweeper and the staging/aesthetics cleanup services
against production data, listening on 8080 inside the Compose network until
somebody notices and stops it. It looks like a hung migration. Check
`docker ps` if `db migrate` does not return within a few seconds.

Read the network name from the running PostgreSQL container as above rather
than guessing it. The Compose project is pinned to `nubarca`, so the network is
attached as `nubarca-internal` — **not** `nubarca_nubarca-internal`. Composing
the `<project>_<network>` form by hand fails with "network not found" and can
read as a migration failure when nothing is wrong with the migration.

A successful run prints its own confirmation, and the second line is the one
that proves the release's role catalogue is in place:

```text
db migrate: applying 1 migration(s):
  + 20260808122509_MakeRolesFirstClass
db migrate: built-in roles verified.
db migrate: completed.
```

Verify the migration by its effect on the data, not only by its exit code:

```bash
docker exec -i nubarca-postgres sh -c \
  "PGPASSWORD=\"\$POSTGRES_PASSWORD\" psql -U <user> -d <db> -At -F' = '" <<'SQL'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId" DESC LIMIT 1;
SQL
```

Use a heredoc for SQL. Nested `ssh "docker exec sh -c \"psql -c ...\""` quoting
strips the inner double quotes, and PostgreSQL then folds every quoted
identifier to lowercase — so `"RoleKey"` arrives as `rolekey` and the query
fails with "column does not exist" against a perfectly good schema.

## 5. Pin and deploy

`docker-compose.release.local.yml` is the RELEASE PIN. It is server-local and
not in the repository, and it names images that already exist — it is no longer
where locally built images are referenced.

Record the currently pinned backend image BEFORE editing: that recorded value is
the whole of the rollback (§9), and it is only obtainable now.

```yaml
services:
  api:
    image: "ghcr.io/<owner>/nubarca-api-openvino@sha256:<digest>"
  worker:
    image: "ghcr.io/<owner>/nubarca-api-openvino@sha256:<digest>"
  frontend:
    image: "ghcr.io/<owner>/nubarca-frontend@sha256:<digest>"
```

API and worker take the SAME digest, because both run the OpenVINO target. The
frontend is a separate image with its own digest, built by a parallel job.

Pin only what the release actually changes. A frontend-only release leaves the
backend pins exactly as they are, and the reverse.

Then recreate only affected services with the complete Compose stack:

```bash
docker compose \
  -f docker-compose.prod.yml \
  -f docker-compose.prod.local.yml \
  -f docker-compose.facedirect-api.yml \
  -f docker-compose.release.local.yml \
  --env-file .env \
  up -d --no-build --no-deps <affected-services>
```

`--no-build` is defence in depth: the production model has no application build
recipes, and the flag makes that invariant explicit in the deploy command and
shell history.

Rules:

- backend runtime changes normally mean `api worker`;
- frontend-only changes mean `frontend`;
- do not recreate PostgreSQL;
- do not restart unrelated sidecars;
- do not enqueue maintenance work unless explicitly requested.

## 6. Mandatory smoke checks

Confirm the effective image tag and running state of every changed container.
For the backend also confirm the running `NUBARCA_GIT_SHA`.

```bash
curl -fsS http://127.0.0.1:8080/health
curl -fsS http://127.0.0.1:8081/
```

Wait until the API Docker health is `healthy`. Confirm the frontend HTML names
the newly built JS/CSS assets. Check worker stability and recent logs without
printing secrets, paths, payloads or raw metadata.

When the backend image changed, also confirm the delivery itself did what it
claims — the container is running the exact bytes that were gated:

```bash
docker inspect nubarca-api    --format '{{.Image}}'
docker inspect nubarca-worker --format '{{.Image}}'
docker exec nubarca-api sh -c 'printf %s "$NUBARCA_GIT_SHA"'
```

Both containers must resolve to the same image, and the running
`NUBARCA_GIT_SHA` must equal the source SHA the digest was built from.

`/health/ready` matters more than `/health` for this stack: readiness is what
the direct-OpenVINO model reports through, so a container that answers `/health`
but never becomes ready has loaded no inference runtime. Also confirm the GPU
wiring actually reached the containers, since the image no longer comes from
this host and a device mount is the one thing an image cannot carry:

```bash
curl -fsS http://127.0.0.1:8080/health/ready
docker exec nubarca-api    ls /dev/dri
docker exec nubarca-worker ls /dev/dri
```

Check the API and worker logs for the OpenVINO provider initialising, and for
any silent fall back to a CPU or synthetic path that was not asked for.

When the FRONTEND image changed, confirm the served bundle is the new one and —
more importantly — that the boundary between the image and the installation's
own artifacts survived:

```bash
docker inspect nubarca-frontend --format '{{.Config.Image}}'
docker inspect nubarca-frontend \
  --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
curl -fsS -o /dev/null -w '%{http_code}\n' http://127.0.0.1:8081/
curl -fsSI "$ORIGIN/download/tv/nubarca-tv.apk" | grep -i content-type
```

The APK check is the one that would catch a real regression: the bundle comes
from the image, the APK comes from a read-only volume this installation mounts,
and replacing the container is exactly when that distinction gets broken. It
must still answer with the APK media type, not the SPA shell.

When backend video processing changed, verify inside the running worker:

```text
Media__VideoPosterProvider=ffmpeg
Media__VideoMetadataProvider=ffprobe
```

## 7. Post-deploy disk check (read-only)

After all smoke checks have passed, record disk usage so a slow capacity leak
from image builds is visible over time. This step is diagnostic only — no
cleanup command is ever run automatically as part of a deploy, including
frontend-only deploys.

```bash
docker system df
df -h / /var/lib/docker
```

If `/` is at or above 90% usage, or has less than 10 GiB available, stop and
flag it to the operator. Reclaiming space is always a separate, explicit
operator decision made outside this runbook, never a step of the deploy
itself.

Prohibited, in this step and in the deploy workflow generally:

- `docker system prune` / `docker system prune -a`
- `docker image prune` / `docker image prune -a`
- `docker volume prune`
- `docker container prune`
- `docker builder prune -af`
- `docker compose down --remove-orphans`

Also never: remove NubArca storage, PostgreSQL, OpenVINO cache or
DataProtection volumes; delete files directly from Docker/containerd data
directories; or unmount storage to make room.

If more space is needed, an operator inspects the remaining tagged images,
volumes and mounts explicitly and makes the cleanup decision outside this
runbook. Do not infer that an apparently unused mount directory is empty: data
hidden below a mount must be checked separately before any destructive action.

Keep the currently pinned image and the immediately previous API/worker/
frontend release tags available for rollback regardless of disk pressure.

## 8. Canonical video-derivative regeneration

Only run this when explicitly requested. First use `jobs list` and do not
enqueue if an equivalent gallery-regeneration job is already queued/running.

To force only video derivatives:

```bash
dotnet NubArca.Api.dll jobs enqueue \
  media-gallery-derivatives-regenerate \
  --sizes poster,video-preview-strip \
  --force
```

Run it through `compose exec -T api` with the complete four-file Compose stack.
Verify that the worker claims the returned job ID. This command excludes
`small`, `medium`, HLS, transcodes, originals and unrelated derivatives.

## 9. Rollback

Keep the previous image references until smoke checks pass. To roll back,
restore the previous pins in `docker-compose.release.local.yml` and recreate
only the affected services with the same four-file Compose stack, again with
`--no-build`.

For the backend this is now a pin change and nothing else: **no recompilation**.
The previous image is still in the local daemon, and the one being rolled back
from remains addressable by its digest, so the rollback is reversible in both
directions and neither leg depends on a build succeeding under pressure. That
property is a large part of why the backend stopped being built here.

For a guided migration release, the tracked policy has explicitly established
that the previous application is compatible with the upgraded schema; the
script may therefore restore the previous image pins after a failed smoke check
while retaining and reporting the verified backup. It does not reverse the
migration automatically.

For any migration outside that policy, use its separately reviewed restore plan
and never assume an image-only rollback is schema-safe.

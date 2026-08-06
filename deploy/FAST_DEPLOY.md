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

Never source `.env`. Let Compose read it through `--env-file .env`; sourcing it
can truncate the semicolon-delimited PostgreSQL connection string.

Do not use `--remove-orphans`. The HumanAesExpert and direct-import containers
may legitimately be managed by separate Compose invocations.

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

## 2. Build immutable images

Backend changes always require both API and worker to use the same image bytes:

```bash
docker build --pull=false \
  --target runtime-openvino \
  --build-arg GIT_SHA=<fullsha> \
  -f src/NubArca.Api/Dockerfile \
  -t nubarca-api:release-<shortsha> .

docker tag \
  nubarca-api:release-<shortsha> \
  nubarca-worker:release-<shortsha>
```

The `--target runtime-openvino` and `GIT_SHA` build argument are mandatory.
Plain `docker build` selects the final lean `runtime` stage and is not a valid
production image for the current OpenVINO deployment.

Frontend changes:

```bash
docker build --pull=false \
  -f frontend/Dockerfile \
  -t nubarca-frontend:release-<shortsha> \
  frontend
```

Build only the services affected by the diff. Documentation/test-only commits
do not require a container rebuild.

## 3. Gate images before changing release pins

Do not edit `docker-compose.release.local.yml` until every required build has
succeeded.

For a backend image, verify:

- `NUBARCA_GIT_SHA` equals the full release SHA;
- `ffmpeg` and `ffprobe` exist;
- `/opt/nubarca/ort-openvino` contains the ONNX Runtime native library. It ships
  under its SONAME, so match the versioned name (`ls
  /opt/nubarca/ort-openvino/libonnxruntime.so*`) — a bare `libonnxruntime.so`
  does not exist and checking for it fails a good image;
- API and worker tags resolve to the same image ID.

For a frontend image, the Docker build must complete `tsc -b` and Vite
successfully.

If a gate fails, leave the current release pins and running containers
untouched.

## 4. Database migration, when present

If and only if the diff contains a new EF migration:

1. create and verify the normal production backup;
2. run `db migrate` with the newly built API image and the complete four-file
   Compose stack;
3. stop if migration fails; do not recreate application containers.

Do not run a migration for releases without migration files.

## 5. Pin and deploy

Update only the relevant image entries in
`docker-compose.release.local.yml`:

```yaml
services:
  api:
    image: "nubarca-api:release-<shortsha>"
  worker:
    image: "nubarca-worker:release-<shortsha>"
  frontend:
    image: "nubarca-frontend:release-<shortsha>"
```

Then recreate only affected services with the complete Compose stack:

```bash
docker compose \
  -f docker-compose.prod.yml \
  -f docker-compose.prod.local.yml \
  -f docker-compose.facedirect-api.yml \
  -f docker-compose.release.local.yml \
  --env-file .env \
  up -d --no-deps <affected-services>
```

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

Keep the previous image tags until smoke checks pass. To roll back, restore the
previous pins in `docker-compose.release.local.yml` and recreate only the
affected services with the same four-file Compose stack. A release containing a
database migration requires the migration-specific restore plan; never assume
an image-only rollback is schema-safe.

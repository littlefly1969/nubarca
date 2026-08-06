# NubArca — first-deploy runbook

This runbook walks an operator from an **empty host** to a verified
NubArca deployment. It assumes you've already read the "Production
deployment" section of the [main README](../README.md) at least once; this
document is the linear, step-by-step companion you can follow once.

Each step has a **why** line so a future maintainer can adapt the runbook
without losing track of intent. Steps are numbered so a partial run can be
resumed.

> **One-line summary.** Stand up a host with Docker + a reverse proxy,
> create `.env`, run `db migrate`, run `users ensure`, `up -d`, hit
> `/health`, run a backup, run a restore dry-run, then upload your first
> real file. **Do NOT upload important data before a successful backup +
> restore drill.**

---

## Prerequisites

- A Linux host (any distro with current Docker + systemd will work).
- Docker Engine ≥ 24 and `docker compose` plugin available.
- A domain name with an A/AAAA record pointing at the host.
- Inbound TCP **80 + 443** reachable from the public internet.
- Inbound TCP **5432** **NOT** reachable from the public internet.
- `curl`, `git`, `openssl`, `tar`, `gzip` on the host (almost certainly
  pre-installed).

---

## 1. Prepare the server

**Why.** A clean baseline rules out half the failure modes you'd otherwise
spend an evening debugging.

```bash
# Verify docker is current and your user can drive it without sudo.
docker version
docker compose version
docker info | grep -i 'server version'

# Confirm date is sane (TLS certs hate clock skew).
date -u
timedatectl status   # ensure NTP / timesync is enabled
```

Set up a host-level firewall (example: `ufw`):

```bash
sudo ufw allow OpenSSH
sudo ufw allow 80,443/tcp
sudo ufw deny 5432/tcp           # belt-and-braces; compose already keeps
                                 # PostgreSQL on the internal network only
sudo ufw enable
sudo ufw status verbose
```

---

## 1b. (Optional) Storage layout — fast root + RAID1

**Why.** On a single server you want the database on the fastest disk and the
bulk, regenerable data on the roomy, redundant disk. This step is **optional**
— skip it and the default Docker-managed named volumes work fine on one disk.
Do it now if at all, because it must happen *before* the volumes are created.

This runbook documents the **production server's actual topology**: a fast
**root filesystem** (~35 GB free) and a **RAID1 array mounted at
`/mnt/raid1`** for everything large.

| What | Where | Why |
|---|---|---|
| PostgreSQL data | **root disk** `/srv/nubarca/postgres` | latency-sensitive (index random I/O, WAL fsync) → put it on the fastest disk |
| Original blobs | **RAID1** `/mnt/raid1/nubarca/blobs` | large + unbounded growth + want redundancy → not the small root disk |
| Local backups | **RAID1** `/mnt/raid1/nubarca/backups` | must NOT share the DB disk — a root-disk failure would lose data *and* its backup |
| Derived artifacts | blob store by default; optional faster cache disk | **see note** |

> **Derived-artifact note.** Derived artifacts (thumbnails, medium
> previews, video posters) are content-addressed blobs that, by default, share
> the original blob store — so with `NUBARCA_BLOB_DATA` on RAID1 they live on
> RAID1 alongside the originals, which is fine. If you later add a faster cache
> disk, point derived artifacts at it with `NUBARCA_DERIVED_DATA` →
> `Storage:DerivedRootPath` (uncomment the `storage-derived-data` volume + mount
> in `docker-compose.prod.yml`). Derived artifacts are **regenerable** cache —
> that disk needs neither redundancy nor backups, and a missing/empty derived
> root self-heals on demand (the thumbnail/preview/poster endpoints regenerate)
> or via `media derivatives backfill` (see §11). On this server (no separate
> cache disk yet) leave `NUBARCA_DERIVED_DATA` unset.

### Prepare the directories (Ubuntu)

PostgreSQL goes in a plain directory on the **existing root filesystem** — no
new mount, just create it:

```bash
sudo mkdir -p /srv/nubarca/postgres
```

The RAID1 array should be mounted at `/mnt/raid1` via fstab by **UUID** (stable
across reboots — never use `/dev/mdX` or `/dev/sdX` in fstab). If it isn't
already mounted:

```bash
# Find the array's filesystem UUID.
lsblk -f
sudo blkid /dev/md0          # adjust to your RAID device

# Add an fstab entry by UUID (ext4 example; adjust fs type to yours).
echo 'UUID=<raid1-uuid>  /mnt/raid1  ext4  defaults,noatime  0 2' | sudo tee -a /etc/fstab

# Mount from fstab and confirm.
sudo systemctl daemon-reload
sudo mount -a
findmnt /mnt/raid1           # confirm it is mounted (and on the RAID device)

# Create the NubArca data directories on RAID1.
sudo mkdir -p /mnt/raid1/nubarca/blobs
sudo mkdir -p /mnt/raid1/nubarca/backups
```

### Point NubArca at them

In `.env` set the host paths:

```
NUBARCA_POSTGRES_DATA=/srv/nubarca/postgres
NUBARCA_BLOB_DATA=/mnt/raid1/nubarca/blobs
BACKUP_DIR=/mnt/raid1/nubarca/backups
# NUBARCA_DERIVED_DATA=/mnt/raid1/nubarca/derived   # reserved, no effect yet
```

Then **uncomment the `driver_opts` bind blocks** for `postgres-data` and
`storage-data` at the bottom of `docker-compose.prod.yml`. The volume *names*
stay `nubarca-postgres-data` / `nubarca-storage-data`, so `backup.sh` /
`restore.sh` keep working unchanged. Validate before continuing:

```bash
docker compose -f docker-compose.prod.yml --env-file .env config > /dev/null \
  && echo "compose OK (bind mounts)"
```

If you set `BACKUP_DIR` in `.env`, either export it before running backups or
pass the path explicitly: `./deploy/backup.sh /mnt/raid1/nubarca/backups`.

> **⚠ Monitor root free space.** PostgreSQL is on the root filesystem, which is
> small (~35 GB). WAL + table growth on a full root disk can wedge the whole
> host — Docker, system logs, and the database all stop. Watch `df -h /` from
> your uptime monitor, or add a cron that warns when free space drops below a
> few GB. The blob store growing on RAID1 is the expected, safe direction; the
> root disk filling is the dangerous one.

---

## 2. Clone the repo

**Why.** Pin to a known tag rather than `main` so an upstream change between
"I wrote the runbook" and "you read it" can't surprise you.

```bash
git clone https://github.com/<your-fork>/nubarca.git
cd nubarca

# Pin to a known release (replace with the tag you intend to deploy).
git checkout v0.x.y
git rev-parse --short HEAD     # note this commit; you'll record it in backups
```

---

## 3. Create `.env`

**Why.** Production secrets live in `.env`, never in tracked files. The
template documents every supported variable.

```bash
cp .env.example .env
chmod 600 .env

# Generate a strong PostgreSQL password (do NOT use a sample value).
echo "POSTGRES_PASSWORD=$(openssl rand -base64 32 | tr -d '/+=' | head -c 40)"

# Edit .env. At minimum, change:
#   POSTGRES_PASSWORD=<above value>
#   ConnectionStrings__Postgres=Host=postgres;...;Password=<same value>
$EDITOR .env
```

Sanity-check that the connection string and `POSTGRES_PASSWORD` are
consistent:

```bash
# Should print the same value twice.
grep ^POSTGRES_PASSWORD .env | cut -d= -f2-
grep ^ConnectionStrings__Postgres .env | sed -E 's/.*Password=([^;]+).*/\1/'
```

Re-confirm `.env` is gitignored (the template is tracked; real `.env` is
not):

```bash
git check-ignore -v .env
# .gitignore:35:.env    .env       ← expected
```

---

## 4. Reverse proxy + TLS

**Why.** The compose stack does NOT terminate TLS. Without a proxy the auth
cookie travels in clear text, which leaks sessions on every request.

Pick one option below. **Replace `nubarca.example.com`** with your real
domain.

### Option A — Caddy (recommended for first deploys)

Caddy auto-provisions Let's Encrypt certificates and handles renewals.

```bash
# Install Caddy per its official docs, then:
sudo cp deploy/Caddyfile.example /etc/caddy/Caddyfile
sudo $EDITOR /etc/caddy/Caddyfile      # replace nubarca.example.com
sudo systemctl reload caddy
sudo journalctl -u caddy --since '1 min ago'   # watch ACME provisioning
```

### Option B — nginx + certbot

```bash
sudo cp deploy/nginx.conf.example /etc/nginx/sites-available/nubarca
sudo $EDITOR /etc/nginx/sites-available/nubarca
sudo ln -sf /etc/nginx/sites-available/nubarca /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
sudo certbot --nginx -d nubarca.example.com
```

Either way, verify from another machine:

```bash
curl -I https://nubarca.example.com/         # 200 from the SPA bundle
curl -sk https://nubarca.example.com/health  # before stack is up: 502/504 is OK
```

**Docker bridge — required with Apache/nginx on the same host.** The API
container receives requests from the Docker bridge gateway (e.g.
`172.18.0.1`), not from `127.0.0.1`. The `ForwardedHeaders` middleware only
trusts loopback by default, so it ignores `X-Forwarded-Proto: https` coming
from a non-loopback IP. The result: the request looks like plain `http://`
inside the container and the CSRF middleware returns **403** on every login.

Fix: find your Docker bridge subnet and add it to `KnownNetworks`:

```bash
# Find the subnet (run on the server)
docker network inspect nubarca-internal \
  --format '{{range .IPAM.Config}}{{.Subnet}}{{end}}'
# Typical output: 172.18.0.0/16
```

Then in `.env`:

```
ForwardedHeaders__KnownNetworks__0=172.18.0.0/16   # use your actual subnet
```

`TrustAny=true` also works here because the API ports are loopback-bound
(`127.0.0.1:8080/8081`) — an external attacker can never reach them to inject
fake headers — but `KnownNetworks` is more precise and preferred.

If the api stack is behind additional hops (a separate load balancer, a cloud
proxy), add those IPs/networks with `KnownProxies__N` / `KnownNetworks__N`.

---

## 5. Validate compose config

**Why.** Catches misspelled variable names, missing requireds, and bad
syntax before any container starts.

```bash
docker compose -f docker-compose.prod.yml --env-file .env config > /dev/null \
  && echo "compose OK"
```

A failure here looks like `POSTGRES_PASSWORD is required` — fix `.env`
before continuing.

---

## 6. Build images + initialise volumes

**Why.** Pulls / builds all three images and creates the two named volumes
(`nubarca-postgres-data`, `nubarca-storage-data`) so subsequent CLI
commands can attach to them.

```bash
docker compose -f docker-compose.prod.yml --env-file .env build
docker compose -f docker-compose.prod.yml --env-file .env up -d postgres
# Wait until postgres is healthy:
docker compose -f docker-compose.prod.yml --env-file .env ps
```

---

## 7. Run `db migrate`

**Why.** Migrations do not auto-apply at api start. Applying
them before the api comes up means the first start sees a ready schema.

```bash
docker compose -f docker-compose.prod.yml --env-file .env run --rm api \
  db migrate
# Expect: "db migrate: applying N migration(s):" then "db migrate: completed."
```

If you see `78: ConnectionStrings:Postgres is not set`, the api container
isn't reading `.env`. Re-check step 3.

> **Startup-migrate alternative.** There is an opt-in
> `Database__MigrateOnStartup` flag (wired through compose, default
> `false`). When `true`, the api applies pending migrations during its own
> startup and fails fast if a migration throws. It is **off by default on
> purpose**: the explicit `db migrate` step above keeps schema changes
> under operator control and is the recommended path, especially on a
> populated database. Only enable startup-migrate for a single-replica
> deploy where you accept the api owning its own schema upgrades — and
> always take a backup first (step 11). Do **not** enable it while running
> more than one api replica.

**Failed migration?** A migration that throws leaves the schema partially
applied. Recover by: (1) restoring the pre-migration backup (step 11), (2)
fixing the cause (usually a connection/permission issue, visible in the
command's stderr — it prints the exception type + message, never secrets),
(3) re-running `db migrate`. `db migrate` is idempotent: a re-run with no
pending migrations is a no-op.

---

## 8. Create the first admin user

**Why.** NubArca has no public registration. Without this step you have a
running stack you cannot log into.

> **Important — how `docker compose run` handles variables.**
> `--env-file .env` substitutes compose variables (service names, ports,
> secrets) but does **not** automatically inject every `.env` line as a
> container environment variable. The `NUBARCA_ADMIN_*` keys are not
> declared in the `environment:` block of the api service (deliberately — you
> don't want the password there permanently), so they never reach the process
> via `--env-file` alone. Pass them explicitly with `-e` flags instead.

```bash
docker compose -f docker-compose.prod.yml --env-file .env run --rm \
  -e NUBARCA_ADMIN_EMAIL=you@example.com \
  -e NUBARCA_ADMIN_DISPLAY_NAME="Your Name" \
  -e NUBARCA_ADMIN_PASSWORD="a-strong-password" \
  -e NUBARCA_ADMIN_IS_ADMIN=true \
  api users ensure
# Expect: "users ensure: created user you@example.com (...) as admin."
```

The credentials are passed only for this one-shot `run` invocation and are
never written to any config file or image layer.

The first user must be created with `NUBARCA_ADMIN_IS_ADMIN=true` so they
can reach `/api/admin/*` — otherwise nobody on the system can hit the operator
endpoints, and you'd have to re-run with `users grant-admin` manually.

Alternatively, you can pass the values as CLI flags to avoid having secrets in
shell history (use a password manager or `read -s` to avoid echoing):

```bash
read -rsp "Password: " PASS && echo
docker compose -f docker-compose.prod.yml --env-file .env run --rm api \
  users ensure \
  --email you@example.com \
  --display-name "Your Name" \
  --password "$PASS" \
  --admin
unset PASS
```

---

## 9. Bring the stack up

```bash
docker compose -f docker-compose.prod.yml --env-file .env up -d
docker compose -f docker-compose.prod.yml --env-file .env ps
docker compose -f docker-compose.prod.yml --env-file .env logs --tail=20 api
```

You should see the api logs settle into "Now listening on:
http://[::]:8080" without exceptions.

### Optional features (all off / conservative by default)

These are wired through `docker-compose.prod.yml` with safe defaults; set
them in `.env` only if you want them. None are required for a working
deployment. Full per-key docs live in `.env.example`.

- **Storage limits.** `Storage__MaxUploadBytes` (default 2 GiB;
  `0` = unlimited) and `Storage__DefaultUserQuotaBytes` (default `0` =
  unlimited per-user logical quota). The upload cap is enforced in the app
  **in addition to** the reverse-proxy body-size limit — set both (e.g.
  nginx `client_max_body_size`, Caddy `request_body max_size`).
- **FFmpeg video posters.** `Media__VideoPosterProvider`
  defaults to `synthetic` (a drawn placeholder, no native dependency). Set
  it to `ffmpeg` **only** if `ffmpeg` is on PATH in the api container — the
  stock image does not bundle it. On any FFmpeg failure the app silently
  falls back to the synthetic poster, so a misconfiguration never breaks
  playback.
- **Background-job worker.** `Jobs__WorkerEnabled` defaults to
  `false` — the api never processes jobs automatically. Either enable the
  in-process worker here, or run jobs out-of-band:
  `docker compose ... run --rm api jobs run-once`.
  Inspect with `jobs list` (counts + recent rows; never prints payloads).
- **Cleanup services.** `BlobJanitor__Enabled` /
  `FileItemSweeper__Enabled` default `false`. Enable both together once you
  want trash to be reclaimed automatically. Their grace windows are
  sequential: Trash retention first, then blob reclamation after the final
  retained owner is permanently removed.

---

## 10. Smoke checks

Run the unauthenticated smoke script:

```bash
BASE_URL=https://nubarca.example.com ./deploy/smoke-check.sh
```

Then do the manual checks (see [SMOKE_CHECKLIST.md](SMOKE_CHECKLIST.md)).
At minimum: open `https://nubarca.example.com/` in a browser, sign in,
upload a tiny file, download it, create a share link, open the link in a
private window, revoke the link.

---

## 11. Backup + restore drill

**Why.** A backup that has never been restored is a `pg_dump` you've never
read. Do this BEFORE uploading anything you care about.

```bash
# 1. Take a baseline backup.
./deploy/backup.sh

# 2. Spot-check what's inside.
ls -la ./backups/
cat ./backups/nubarca-*/manifest.json | head -20

# 3. Validate the backup is well-formed without mutating anything.
./deploy/restore.sh ./backups/nubarca-* --yes   # ← only on a SEPARATE drill host
# On the production host, the dry-run is the appropriate check:
./deploy/restore.sh ./backups/nubarca-*
```

If you only have the production host, the dry-run is the safe substitute
on that host. The actual `--yes` restore must run on a **separate** drill
host (e.g. a VM); see [SMOKE_CHECKLIST.md](SMOKE_CHECKLIST.md) for the
8-step drill.

Only after a successful drill is the deployment "trusted enough" for real
data.

**What's in the backup — required vs regenerable.** The dump
(`postgres.sql.gz`) and the original blobs are the **required** data: losing
either loses information. Derived artifacts (thumbnails, medium previews,
video posters) are **regenerable** from the originals via
`media derivatives backfill`. `backup.sh` takes a **full** backup — DB plus
the entire blob store. With the default single root that archive already
includes the derived artifacts; with a separate `Storage:DerivedRootPath`
 a full backup must also capture the derived root. An **essential**
backup (DB + originals only) is valid too — skip the derived root and, after
restore, run `media derivatives backfill` (or just let the endpoints
regenerate on demand) to rebuild thumbnails/previews/posters. The DB dump and
the **originals** archive are always a **matched pair** — restore them
together; the derived data is never required.

---

## 12. Lock down

- Since step 8 uses `-e` flags (not `.env` entries), no `.env` cleanup is
  needed for `NUBARCA_ADMIN_*`. If you did add those lines to `.env` as a
  temporary measure, remove or comment them out now.
- Confirm the firewall is enabled and PostgreSQL is not exposed:
  `sudo ufw status verbose` — expect 22/80/443 allowed, 5432 absent.
- Confirm `git log --oneline -5` matches the tag you intended to deploy.
- Tell yourself out loud which off-host backup target you're using. If you
  can't name one, configure it before storing data.

---

## 13. Upgrade workflow

**Why.** Code and schema move together. Always back up first; apply
migrations explicitly; rebuild; restart. The sequence below is safe to
repeat for every release.

```bash
# 1. Take a fresh backup FIRST (so you can roll back to the pre-upgrade state).
./deploy/backup.sh

# 2. Fetch the new code and pin to the target tag.
git fetch --tags
git checkout v0.x.z          # the release you are upgrading to
git rev-parse --short HEAD   # record it

# 3. Rebuild the images for the new code.
docker compose -f docker-compose.prod.yml --env-file .env build

# 4. Apply pending migrations BEFORE the new api starts (recommended path).
docker compose -f docker-compose.prod.yml --env-file .env run --rm api \
  db migrate

# 5. Recreate the containers with the new images.
docker compose -f docker-compose.prod.yml --env-file .env up -d

# 6. Verify.
BASE_URL=https://nubarca.example.com ./deploy/smoke-check.sh
docker compose -f docker-compose.prod.yml --env-file .env logs --tail=30 api
```

Migrations are additive; still, step
1 is non-negotiable — it is the only thing that makes step-by-step rollback
possible.

## 14. Rollback notes

There is no in-place "undo migration" path. Roll back by **restoring the
pre-upgrade backup** and checking out the previous tag:

```bash
# 1. Check out the version the backup was taken from (manifest.json records
#    the gitRef).
git checkout <previous-tag>
docker compose -f docker-compose.prod.yml --env-file .env build

# 2. Restore the pre-upgrade backup on this host (DESTRUCTIVE — replaces
#    current volumes). On a host with live post-upgrade data you care about,
#    take a fresh backup of THAT first so the rollback itself is reversible.
./deploy/restore.sh ./backups/<pre-upgrade-backup>          # dry-run first
./deploy/restore.sh ./backups/<pre-upgrade-backup> --yes    # then apply

# 3. Bring the old version up and smoke-check.
docker compose -f docker-compose.prod.yml --env-file .env up -d
BASE_URL=https://nubarca.example.com ./deploy/smoke-check.sh
```

Because code + schema move as a pair, never run a new code image against a
restored old schema (or vice-versa) for longer than the rollback itself —
mismatched pairs are unsupported.

## 15. Log inspection

```bash
# api application logs (most useful for auth / migration / job issues).
docker compose -f docker-compose.prod.yml --env-file .env logs api --tail=100 -f

# postgres + frontend.
docker compose -f docker-compose.prod.yml --env-file .env logs postgres --tail=50
docker compose -f docker-compose.prod.yml --env-file .env logs frontend --tail=50

# everything since a timestamp.
docker compose -f docker-compose.prod.yml --env-file .env logs --since 10m
```

Logs are designed to be safe to share: errors are sanitized to an exception
type + short message (background-job failures, migration failures, FFmpeg
fallbacks), and the app never logs storage keys, physical paths, raw
metadata, GPS coordinates, serials, tokens, or passwords. The reverse proxy
(Caddy/nginx) keeps its own access/error logs — point your uptime monitor
at `/health`.

## 16. PostgreSQL maintenance (occasional)

NubArca needs no special tuning, but a few periodic checks keep the admin
Storage Stats page and large admin imports fast. None require exposing
PostgreSQL publicly or any external monitoring stack.

```bash
# Shell into the DB (psql is inside the postgres container).
docker compose -f docker-compose.prod.yml --env-file .env exec postgres \
  psql -U "$POSTGRES_USER" -d "$POSTGRES_DB"
```

- **ANALYZE after a big import.** A large admin import inserts many rows; run
  `ANALYZE;` (or `VACUUM (ANALYZE);`) afterwards so the planner has fresh
  statistics for the FileItem/Blob indexes. Autovacuum normally handles this,
  but a one-off `ANALYZE;` right after a bulk import is worth it.
- **Autovacuum is on by default** — leave it on. Only run a manual
  `VACUUM (ANALYZE) <table>;` if you've just deleted/purged a very large
  number of rows (e.g. emptying a huge trash) and want space reclaimed sooner.
- **REINDEX only if justified** — e.g. a specific index is visibly bloated
  after massive churn (`\di+` shows its size). It is rarely needed; don't run
  it routinely.
- **See what's slow right now:** `SELECT pid, state, wait_event_type, query
  FROM pg_stat_activity WHERE state <> 'idle';` (the `query` text stays inside
  the DB shell — never copy it anywhere that isn't admin-only). If the
  `pg_stat_statements` extension is enabled you can also rank queries by total
  time; it is optional and not required by NubArca.
- **Storage Stats slow?** The admin page now shows a per-phase timing line
  ("Computed in N ms · physical scan … · metadata …") and caches the result
  for ~30s; hit **Refresh** to force a recompute. If "physical scan" dominates,
  the bottleneck is the blob-store filesystem walk, not PostgreSQL.

---

## What this runbook does NOT cover

- **Automatic certificate renewal monitoring.** Caddy + certbot both renew
  on their own; subscribe to your reverse proxy's logs to catch breakage
  rather than discovering it via expired TLS in a browser tab.
- **Off-host backup shipping.** `./deploy/backup.sh` writes to local disk.
  Use `rclone copy ./backups/ <remote>`, `restic backup`, or `scp` to ship
  copies elsewhere — a future helper may be bundled.
- **Hot backups.** Backups are cold-only: the script stops api + frontend
  briefly. PostgreSQL streaming replication / PITR is the next-step
  upgrade once you have repeat traffic.
- **Monitoring / alerting.** Health endpoint is enough for a uptime
  monitor (UptimeRobot, healthchecks.io, Prometheus blackbox exporter).
  Pick one before the deployment matters.
- **User self-service.** There's no public registration — `users ensure`
  is the only path to add users. Run it again for every new user.

---

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `db migrate` returns 78 | `ConnectionStrings__Postgres` missing in `.env` | Re-edit `.env`; confirm with `docker compose config` |
| `db migrate` runtime failure | Connection string points at a host the container can't reach | Inside the compose network use `Host=postgres` not `localhost` |
| `users ensure` says "created" but login 401s | Wrong email casing | Emails are normalised to lower-case server-side; type lower-case |
| Login returns **403** (browser console: `POST /api/auth/login 403`) | Docker bridge IP not trusted by ForwardedHeaders | Add `ForwardedHeaders__KnownNetworks__0=<docker-subnet>` in `.env` (see step 4). The middleware ignores `X-Forwarded-Proto` from an untrusted IP → request looks like `http://` → CSRF blocks it. |
| `/health` returns 502 from the proxy | Stack hasn't finished starting | `docker compose logs api`; wait 5–10 s and retry |
| `/health` returns 200 but login redirects to `/login` again | `Secure` cookie + plain HTTP | Force HTTPS; cookie is only sent over the matching scheme |
| `Could not load existing links` in Share panel after deploy | Stale browser bundle | Hard-reload; the frontend image cache-busts via Vite's hashed asset names |
| Backup script aborts on "POSTGRES_USER is not set" | `.env` typo / missing | Inspect with `grep ^POSTGRES_ .env` |

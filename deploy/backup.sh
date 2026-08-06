#!/usr/bin/env bash
# NubArca — production backup.
#
# Takes a *cold* backup (services stopped) of the PostgreSQL database AND the
# blob storage volume as a matched pair, then restarts the stack. Online
# (hot) backups are deliberately out of scope for this first cut: holding the
# two snapshots consistent without app downtime needs PG point-in-time
# recovery + a quiesced filesystem snapshot, both of which require operator
# choices we don't want to bake into a default script.
#
# Output: a directory under $BACKUP_DIR named `nubarca-YYYYMMDDTHHMMSSZ/`,
# containing:
#   - postgres.sql.gz       gzipped `pg_dump --format=plain --no-owner`
#   - storage.tar.gz        gzipped tar of /var/lib/nubarca/storage
#   - manifest.json         metadata (timestamp, git ref, file checksums)
#
# What's required vs regenerable:
#   REQUIRED    PostgreSQL (postgres.sql.gz) + ORIGINAL blobs. These are the
#               only things you cannot recreate; losing either loses data.
#   REGENERABLE Derived artifacts — image thumbnails, medium previews, video
#               posters. They are content-addressed blobs derived from the
#               originals and can be rebuilt with `media derivatives backfill`.
#
# Backup modes:
#   This script tars the ORIGINAL blob volume (nubarca-storage-data) only.
#   FULL      DB + originals + derived. In the DEFAULT single-root layout
#             (Storage__DerivedRootPath unset) derived artifacts share the
#             original volume, so storage.tar.gz already captures them — this
#             script's output IS a full backup.
#   ESSENTIAL DB + originals, no derived. If you split derived artifacts onto a
#             separate root/volume (Storage__DerivedRootPath +
#             nubarca-storage-derived-data), this script does NOT tar that
#             volume, so its output is an ESSENTIAL backup. That's fine —
#             derived artifacts are regenerable cache: after restore run
#             `dotnet NubArca.Api.dll media derivatives backfill` (or just
#             let the thumbnail/preview/poster endpoints regenerate on demand).
#             To capture derived bytes too, additionally tar the derived volume
#             yourself, e.g.:
#               docker run --rm -v nubarca-storage-derived-data:/d:ro \
#                 -v "$PWD:/out" alpine:3 tar czf /out/derived.tar.gz -C /d .
#   Required matched pair: postgres.sql.gz + storage.tar.gz (DB + originals).
#
# Usage:
#   ./deploy/backup.sh [BACKUP_DIR]
# Environment:
#   BACKUP_DIR           target root (default ./backups)
#   ENV_FILE             docker-compose env file (default ./.env)
#   COMPOSE_FILE         compose file (default docker-compose.prod.yml)
#   NUBARCA_KEEP_UP    if "true", skip the stop / restart (NOT recommended
#                        for the first backup — leaves window for DB/storage
#                        divergence)

set -Eeuo pipefail

# -------- helpers ----------------------------------------------------------

log()  { printf '[backup] %s\n' "$*"; }
fail() { printf '[backup] error: %s\n' "$*" >&2; exit 1; }
trap 'fail "aborted at line $LINENO"' ERR

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.prod.yml}"
ENV_FILE="${ENV_FILE:-.env}"
BACKUP_DIR="${1:-${BACKUP_DIR:-./backups}}"
KEEP_UP="${NUBARCA_KEEP_UP:-false}"

[ -f "$COMPOSE_FILE" ] || fail "compose file not found: $COMPOSE_FILE"
[ -f "$ENV_FILE" ]     || fail "env file not found: $ENV_FILE  (copy .env.example to .env)"

# pg_dump credentials come from the compose environment; we deliberately do
# NOT read POSTGRES_PASSWORD here so we never echo it. POSTGRES_USER and
# POSTGRES_DB ARE non-secret, so we resolve them via `docker compose config`
# to fail fast if they're missing.
pg_user="$(docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" config --format json \
    | python3 -c 'import json,sys;print(json.load(sys.stdin)["services"]["postgres"]["environment"]["POSTGRES_USER"])' 2>/dev/null || true)"
pg_db="$(docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" config --format json \
    | python3 -c 'import json,sys;print(json.load(sys.stdin)["services"]["postgres"]["environment"]["POSTGRES_DB"])' 2>/dev/null || true)"

# Fallback: read straight from $ENV_FILE if python+jq path is unavailable.
if [ -z "$pg_user" ] || [ -z "$pg_db" ]; then
    # shellcheck disable=SC1090
    set -a; . "$ENV_FILE"; set +a
    pg_user="${POSTGRES_USER:-}"
    pg_db="${POSTGRES_DB:-}"
fi

[ -n "$pg_user" ] || fail "POSTGRES_USER is not set in $ENV_FILE"
[ -n "$pg_db" ]   || fail "POSTGRES_DB is not set in $ENV_FILE"

# -------- prepare target ---------------------------------------------------

stamp="$(date -u +'%Y%m%dT%H%M%SZ')"
target="$BACKUP_DIR/nubarca-$stamp"

mkdir -p "$target"
chmod 700 "$target"

git_ref="$(git -C "$REPO_ROOT" rev-parse --short HEAD 2>/dev/null || echo 'unknown')"

log "target: $target"
log "git ref: $git_ref"
log "compose file: $COMPOSE_FILE"
log "env file: $ENV_FILE  (postgres user='$pg_user', db='$pg_db')"

# -------- quiesce ----------------------------------------------------------

restart_on_exit=false
if [ "$KEEP_UP" = "true" ]; then
    log "NUBARCA_KEEP_UP=true — taking ONLINE backup. DB and storage may"
    log "diverge if writes land between the two snapshots. Use only for"
    log "low-traffic windows."
else
    log "stopping api + frontend (postgres stays up so pg_dump can connect)"
    docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" stop api frontend >/dev/null
    restart_on_exit=true
fi

cleanup() {
    if [ "$restart_on_exit" = "true" ]; then
        log "restarting api + frontend"
        docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" start api frontend >/dev/null || true
    fi
}
trap cleanup EXIT

# -------- pg_dump ----------------------------------------------------------

log "pg_dump → postgres.sql.gz"
# pg_dump runs inside the postgres container. It reads POSTGRES_PASSWORD from
# the container's own environment via the standard libpq lookup, so we don't
# have to pass it on the host shell.
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres \
    sh -c "PGPASSWORD=\"\$POSTGRES_PASSWORD\" pg_dump -U \"$pg_user\" -d \"$pg_db\" --format=plain --no-owner --no-privileges" \
    | gzip -c > "$target/postgres.sql.gz"

[ -s "$target/postgres.sql.gz" ] || fail "pg_dump produced an empty file"

# -------- storage tar ------------------------------------------------------

log "tar storage volume → storage.tar.gz"
# Mount the named volume read-only into a throwaway alpine container; tar
# from inside so we don't need to know the host-side path of the volume.
docker run --rm \
    -v nubarca-storage-data:/storage:ro \
    -v "$(cd "$target" && pwd):/out" \
    alpine:3 sh -c "cd /storage && tar czf /out/storage.tar.gz ."

# -------- manifest ---------------------------------------------------------

sql_sha=$(sha256sum "$target/postgres.sql.gz" | awk '{print $1}')
sql_size=$(wc -c < "$target/postgres.sql.gz")
storage_sha=$(sha256sum "$target/storage.tar.gz" | awk '{print $1}')
storage_size=$(wc -c < "$target/storage.tar.gz")

cat > "$target/manifest.json" <<EOF
{
  "timestamp": "$stamp",
  "gitRef": "$git_ref",
  "composeFile": "$COMPOSE_FILE",
  "postgres": {
    "user": "$pg_user",
    "database": "$pg_db",
    "dumpFile": "postgres.sql.gz",
    "sha256": "$sql_sha",
    "sizeBytes": $sql_size
  },
  "storage": {
    "volume": "nubarca-storage-data",
    "archive": "storage.tar.gz",
    "sha256": "$storage_sha",
    "sizeBytes": $storage_size
  },
  "warning": "DB dump and storage archive MUST be restored together. A mixed restore (Postgres from snapshot A, storage from snapshot B) leaves dangling FileItem rows or orphan blobs."
}
EOF

chmod 600 "$target/postgres.sql.gz" "$target/storage.tar.gz" "$target/manifest.json"

log "done: $target"
log "  postgres.sql.gz  $(numfmt --to=iec --suffix=B "$sql_size" 2>/dev/null || echo "$sql_size bytes")"
log "  storage.tar.gz   $(numfmt --to=iec --suffix=B "$storage_size" 2>/dev/null || echo "$storage_size bytes")"
log "  manifest.json    sha256=$sql_sha / $storage_sha"
log
log "Next steps:"
log "  - copy $target off-host (scp / rclone / restic / a USB key for the brave)"
log "  - run a restore drill on a separate host BEFORE you store anything important"

#!/usr/bin/env bash
# NubArca — production restore.
#
# Destructive operation. Restores a backup produced by deploy/backup.sh INTO
# the postgres-data + storage-data named volumes used by docker-compose.prod.yml.
# The script requires `--yes` to actually proceed; without it, it only
# inspects the backup and prints what it would do.
#
# Intended usage on a FRESH host:
#   1. Clone the repo and create .env from .env.example.
#   2. Run `docker compose -f docker-compose.prod.yml --env-file .env up -d`
#      ONCE so the named volumes exist (this also gives you an empty schema
#      via `db migrate` if you ran that first — either way is fine; the
#      restore overwrites everything).
#   3. Run `deploy/restore.sh /path/to/nubarca-YYYYMMDDTHHMMSSZ --yes`.
#
# DANGER: running this on a host that already has live data will permanently
# destroy that data. The script stops the stack, replaces both volumes, and
# brings the stack back up. There is no rollback.
#
# Usage:
#   ./deploy/restore.sh <backup-dir> [--yes]
# Environment:
#   ENV_FILE      docker-compose env file (default ./.env)
#   COMPOSE_FILE  compose file (default docker-compose.prod.yml)

set -Eeuo pipefail

log()  { printf '[restore] %s\n' "$*"; }
fail() { printf '[restore] error: %s\n' "$*" >&2; exit 1; }
trap 'fail "aborted at line $LINENO"' ERR

if [ $# -lt 1 ]; then
    cat <<EOF >&2
Usage: $0 <backup-dir> [--yes]

  <backup-dir>   Path to a directory produced by deploy/backup.sh.
                 Must contain manifest.json + postgres.sql.gz + storage.tar.gz.
  --yes          REQUIRED to actually mutate volumes. Without it the script
                 only prints what it would do (dry-run).

Examples:
  $0 ./backups/nubarca-20260524T120000Z              # dry-run
  $0 ./backups/nubarca-20260524T120000Z --yes        # do it
EOF
    exit 64
fi

BACKUP_DIR="$1"
CONFIRM="${2:-}"

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

COMPOSE_FILE="${COMPOSE_FILE:-docker-compose.prod.yml}"
ENV_FILE="${ENV_FILE:-.env}"

[ -d "$BACKUP_DIR" ]          || fail "backup directory not found: $BACKUP_DIR"
[ -f "$COMPOSE_FILE" ]        || fail "compose file not found: $COMPOSE_FILE"
[ -f "$ENV_FILE" ]            || fail "env file not found: $ENV_FILE"
[ -f "$BACKUP_DIR/manifest.json" ]   || fail "missing manifest.json in $BACKUP_DIR"
[ -f "$BACKUP_DIR/postgres.sql.gz" ] || fail "missing postgres.sql.gz in $BACKUP_DIR"
[ -f "$BACKUP_DIR/storage.tar.gz" ]  || fail "missing storage.tar.gz in $BACKUP_DIR"

# -------- verify checksums -------------------------------------------------

log "verifying sha256 against manifest..."

# Pull expected sha values from the manifest using a portable awk fallback so
# we don't add a `jq` dependency.
expected_sql_sha=$(awk -F'"' '/"sha256":/ && !seen_sql {print $4; seen_sql=1}' "$BACKUP_DIR/manifest.json")
expected_storage_sha=$(awk -F'"' '/"sha256":/ {last=$4} END {print last}' "$BACKUP_DIR/manifest.json")

actual_sql_sha=$(sha256sum "$BACKUP_DIR/postgres.sql.gz" | awk '{print $1}')
actual_storage_sha=$(sha256sum "$BACKUP_DIR/storage.tar.gz" | awk '{print $1}')

[ "$expected_sql_sha" = "$actual_sql_sha" ] \
    || fail "postgres.sql.gz checksum mismatch: expected $expected_sql_sha, got $actual_sql_sha"
[ "$expected_storage_sha" = "$actual_storage_sha" ] \
    || fail "storage.tar.gz checksum mismatch: expected $expected_storage_sha, got $actual_storage_sha"

log "checksums match."

# Resolve POSTGRES_USER / POSTGRES_DB from .env so pg_restore can target the
# right database. POSTGRES_PASSWORD is intentionally NOT echoed here; pg_dump
# is read via the container's environment.
# shellcheck disable=SC1090
set -a; . "$ENV_FILE"; set +a
[ -n "${POSTGRES_USER:-}" ] || fail "POSTGRES_USER is not set in $ENV_FILE"
[ -n "${POSTGRES_DB:-}" ]   || fail "POSTGRES_DB is not set in $ENV_FILE"

# -------- summary ----------------------------------------------------------

log "manifest: $BACKUP_DIR/manifest.json"
sed -n 's/^/[restore]    /p' "$BACKUP_DIR/manifest.json"
log
log "would restore into:"
log "  postgres database '$POSTGRES_DB' as '$POSTGRES_USER'"
log "  named volume 'nubarca-storage-data'"

if [ "$CONFIRM" != "--yes" ]; then
    log
    log "DRY RUN. No changes were made. Re-run with --yes to proceed."
    exit 0
fi

# -------- destructive: stop services + drop existing data ------------------

log
log "STOPPING stack (api + frontend + postgres)..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" stop api frontend postgres >/dev/null || true

# Recreate the postgres volume from scratch — drops any existing rows.
# Using docker compose down keeps the network around but is also fine; we
# explicitly call `docker volume rm` for clarity.
log "REMOVING old postgres volume contents..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" rm -fsv postgres >/dev/null || true
docker volume rm nubarca-postgres-data >/dev/null 2>&1 || true

log "REMOVING old storage volume contents..."
docker volume rm nubarca-storage-data >/dev/null 2>&1 || true

log "starting postgres (empty)..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d postgres >/dev/null

# Wait for postgres to be healthy. We poll pg_isready up to 60 s.
log "waiting for postgres to accept connections..."
for _ in $(seq 1 30); do
    if docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres \
        sh -c "pg_isready -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\"" >/dev/null 2>&1; then
        break
    fi
    sleep 2
done

# -------- restore database ------------------------------------------------

log "psql restoring postgres.sql.gz (size $(wc -c < "$BACKUP_DIR/postgres.sql.gz") bytes)..."
gunzip -c "$BACKUP_DIR/postgres.sql.gz" \
    | docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" exec -T postgres \
        sh -c "PGPASSWORD=\"\$POSTGRES_PASSWORD\" psql -U \"$POSTGRES_USER\" -d \"$POSTGRES_DB\" --quiet --set ON_ERROR_STOP=on"

# -------- restore storage --------------------------------------------------

log "untar storage.tar.gz into nubarca-storage-data..."
docker run --rm \
    -v nubarca-storage-data:/storage \
    -v "$(cd "$BACKUP_DIR" && pwd):/in:ro" \
    alpine:3 sh -c "cd /storage && tar xzf /in/storage.tar.gz"

# -------- bring api + frontend back up ------------------------------------

log "starting api + frontend..."
docker compose -f "$COMPOSE_FILE" --env-file "$ENV_FILE" up -d api frontend >/dev/null

log
log "restore complete."
log "verify next:"
log "  1. curl http://127.0.0.1:8080/health           # api alive"
log "  2. log in as your existing admin user; if the dump pre-dates a"
log "     schema change you may need:"
log "       docker compose -f $COMPOSE_FILE --env-file $ENV_FILE \\"
log "         run --rm api db migrate"
log "  3. spot-check a few downloads to confirm storage objects match."
log "  4. derived artifacts (thumbnails / previews / posters) ride along in"
log "     storage.tar.gz, so a normal restore already has them. If you ever"
log "     restored an ESSENTIAL backup (DB + originals only), regenerate them:"
log "       docker compose -f $COMPOSE_FILE --env-file $ENV_FILE \\"
log "         run --rm api media derivatives backfill"

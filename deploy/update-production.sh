#!/usr/bin/env bash
# Check for, or apply, one CI-built NubArca production release from the server.
# This script never builds and never prunes. Reviewed additive migrations may
# run only after an explicit confirmation and a verified pre-migration backup.

set -Eeuo pipefail

log() { printf '[production-update] %s\n' "$*"; }
fail() { printf '[production-update] error: %s\n' "$*" >&2; exit 1; }

usage() {
    cat >&2 <<'EOF'
usage:
  ./deploy/update-production.sh check --env-file <production.env>
  ./deploy/update-production.sh apply --env-file <production.env> --confirm <full-main-sha> [--confirm-migrations]
EOF
    exit 2
}

[[ $# -ge 1 ]] || usage
MODE="$1"
shift
[[ "$MODE" == "check" || "$MODE" == "apply" ]] || usage
ENV_FILE=""
CONFIRM_SHA=""
CONFIRM_MIGRATIONS=false
while [[ $# -gt 0 ]]; do
    case "$1" in
        --env-file) [[ $# -ge 2 ]] || usage; ENV_FILE="$2"; shift 2 ;;
        --confirm) [[ $# -ge 2 ]] || usage; CONFIRM_SHA="$2"; shift 2 ;;
        --confirm-migrations) CONFIRM_MIGRATIONS=true; shift ;;
        *) usage ;;
    esac
done
[[ -n "$ENV_FILE" ]] || fail "--env-file is required"
[[ "$MODE" == "check" && -z "$CONFIRM_SHA" && "$CONFIRM_MIGRATIONS" == false ]] || \
    [[ "$MODE" == "apply" && "$CONFIRM_SHA" =~ ^[0-9a-f]{40}$ ]] || usage

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
[[ -f "$ENV_FILE" ]] || fail "environment file not found: $ENV_FILE"
for command in git docker python3 curl gzip sha256sum flock; do
    command -v "$command" >/dev/null || fail "$command is required"
done
[[ "$(git branch --show-current)" == "main" ]] || fail "production checkout must be on main"
[[ -z "$(git status --porcelain)" ]] || fail "production checkout is dirty"

if [[ "$MODE" == "apply" ]]; then
    exec 9>/tmp/nubarca-production-update.lock
    flock -n 9 || fail "another production update is already running"
fi

remote="$(git remote get-url origin)"
if [[ "$remote" =~ github\.com[:/]([^/]+)/[^/]+(\.git)?$ ]]; then
    GHCR_OWNER="$(printf '%s' "${BASH_REMATCH[1]}" | tr '[:upper:]' '[:lower:]')"
else
    fail "origin must be a GitHub repository so the GHCR namespace can be derived"
fi

log "fetching origin/main"
git fetch --quiet origin main
CANDIDATE_SHA="$(git rev-parse origin/main)"
HEAD_SHA="$(git rev-parse HEAD)"
[[ "$MODE" != "apply" || "$CONFIRM_SHA" == "$CANDIDATE_SHA" ]] || \
    fail "origin/main moved: confirmed $CONFIRM_SHA, now $CANDIDATE_SHA; run check again"

COMPOSE_BASE=(
    docker compose
    -f docker-compose.prod.yml
    -f docker-compose.prod.local.yml
    -f docker-compose.facedirect-api.yml
    -f docker-compose.release.local.yml
    --env-file "$ENV_FILE"
)
for file in docker-compose.prod.yml docker-compose.prod.local.yml \
    docker-compose.facedirect-api.yml docker-compose.release.local.yml; do
    [[ -f "$file" ]] || fail "required production file is missing: $file"
done

read_env_value() {
    local key="$1" value
    value="$(sed -n -E "s/^[[:space:]]*(export[[:space:]]+)?${key}=//p" "$ENV_FILE" | tail -n 1)"
    value="${value%\"}"; value="${value#\"}"
    value="${value%\'}"; value="${value#\'}"
    printf '%s' "$value"
}

POSTGRES_USER=""
POSTGRES_DB=""
BACKUP_ROOT=""
DATABASE_SIZE_BYTES=""
BACKUP_AVAILABLE_KB=""
MIGRATION_BACKUP=""
MIGRATION_BACKUP_SHA=""

postgres_psql() {
    "${COMPOSE_BASE[@]}" exec -T postgres sh -c \
        'PGPASSWORD="$POSTGRES_PASSWORD" exec psql -v ON_ERROR_STOP=1 -U "$1" -d "$2" -At' \
        sh "$POSTGRES_USER" "$POSTGRES_DB"
}

load_migration_backup_preflight() {
    mapfile -t postgres_identity < <(
        "${COMPOSE_BASE[@]}" config --format json |
            python3 -c 'import json,sys
p=json.load(sys.stdin)["services"]["postgres"]["environment"]
print(p.get("POSTGRES_USER", "")); print(p.get("POSTGRES_DB", ""))'
    )
    [[ ${#postgres_identity[@]} -eq 2 ]] || fail "cannot resolve PostgreSQL identity"
    POSTGRES_USER="${postgres_identity[0]}"
    POSTGRES_DB="${postgres_identity[1]}"
    [[ "$POSTGRES_USER" =~ ^[A-Za-z0-9_.-]+$ ]] || fail "invalid POSTGRES_USER in effective Compose model"
    [[ "$POSTGRES_DB" =~ ^[A-Za-z0-9_.-]+$ ]] || fail "invalid POSTGRES_DB in effective Compose model"

    BACKUP_ROOT="$(read_env_value BACKUP_DIR)"
    [[ "$BACKUP_ROOT" == /* && "$BACKUP_ROOT" != "/" ]] || \
        fail "BACKUP_DIR must be an absolute non-root path for automated migrations"
    [[ -d "$BACKUP_ROOT" && -w "$BACKUP_ROOT" ]] || \
        fail "BACKUP_DIR must already exist and be writable: $BACKUP_ROOT"

    DATABASE_SIZE_BYTES="$(postgres_psql <<'SQL'
SELECT pg_database_size(current_database());
SQL
)"
    [[ "$DATABASE_SIZE_BYTES" =~ ^[0-9]+$ ]] || fail "cannot resolve production database size"
    BACKUP_AVAILABLE_KB="$(df -Pk "$BACKUP_ROOT" | awk 'NR==2 {print $4}')"
    [[ "$BACKUP_AVAILABLE_KB" =~ ^[0-9]+$ ]] || fail "cannot resolve BACKUP_DIR free space"
    required_backup_kb=$(( (DATABASE_SIZE_BYTES * 12 / 10 + 1023) / 1024 + 1048576 ))
    [[ "$BACKUP_AVAILABLE_KB" -ge "$required_backup_kb" ]] || \
        fail "BACKUP_DIR lacks conservative pre-migration capacity (database bytes=$DATABASE_SIZE_BYTES)"
}

create_migration_backup() {
    local stamp short_sha final partial checksum_partial
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    short_sha="${CANDIDATE_SHA:0:12}"
    final="$BACKUP_ROOT/pre-update-${short_sha}-${stamp}.sql.gz"
    partial="$final.partial"
    checksum_partial="$final.sha256.partial"
    [[ ! -e "$final" && ! -e "$partial" && ! -e "$checksum_partial" ]] || \
        fail "pre-migration backup target already exists: $final"
    umask 077

    log "creating pre-migration PostgreSQL backup: $final"
    if ! "${COMPOSE_BASE[@]}" exec -T postgres sh -c \
        'PGPASSWORD="$POSTGRES_PASSWORD" exec pg_dump -U "$1" -d "$2" --format=plain --no-owner --no-privileges' \
        sh "$POSTGRES_USER" "$POSTGRES_DB" | gzip -c > "$partial"; then
        rm -f -- "$partial" "$checksum_partial"
        fail "pre-migration pg_dump failed; release pins and containers are unchanged"
    fi
    [[ -s "$partial" ]] || { rm -f -- "$partial"; fail "pre-migration backup is empty"; }
    gzip -t "$partial" || { rm -f -- "$partial"; fail "pre-migration backup failed gzip verification"; }
    if ! python3 deploy/verify-production-db-backup.py "$partial"; then
        rm -f -- "$partial" "$checksum_partial"
        fail "pre-migration backup failed content verification"
    fi
    MIGRATION_BACKUP_SHA="$(sha256sum "$partial" | awk '{print $1}')"
    mv -- "$partial" "$final"
    printf '%s  %s\n' "$MIGRATION_BACKUP_SHA" "$(basename "$final")" > "$checksum_partial"
    mv -- "$checksum_partial" "$final.sha256"
    MIGRATION_BACKUP="$final"
    log "verified pre-migration backup sha256=$MIGRATION_BACKUP_SHA"
}

run_candidate_migrations() {
    local network applied migration_id
    network="$(docker inspect nubarca-postgres \
        --format '{{range $key,$value := .NetworkSettings.Networks}}{{println $key}}{{end}}' |
        awk 'NF {print; exit}')"
    [[ -n "$network" ]] || fail "cannot resolve the running PostgreSQL container network"
    log "applying ${#MIGRATION_IDS[@]} approved migration(s) with the verified candidate image"
    docker run --rm --network "$network" --env-file "$ENV_FILE" "$NEW_API_IMAGE" db migrate

    applied="$(postgres_psql <<'SQL'
SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";
SQL
)"
    for migration_id in "${MIGRATION_IDS[@]}"; do
        grep -Fxq "$migration_id" <<< "$applied" || \
            fail "migration command returned successfully but history lacks $migration_id"
    done
    log "migration history verified: ${MIGRATION_IDS[*]}"
}

mapfile -t CURRENT_IMAGES < <(
    "${COMPOSE_BASE[@]}" --profile worker config --format json |
        python3 -c 'import json,sys
s=json.load(sys.stdin)["services"]
for name in ("api","worker","frontend"): print(s[name]["image"])'
)
[[ ${#CURRENT_IMAGES[@]} -eq 3 ]] || fail "cannot resolve current release pins"
CURRENT_API_IMAGE="${CURRENT_IMAGES[0]}"
CURRENT_WORKER_IMAGE="${CURRENT_IMAGES[1]}"
CURRENT_FRONTEND_IMAGE="${CURRENT_IMAGES[2]}"
[[ "$CURRENT_API_IMAGE" == "$CURRENT_WORKER_IMAGE" ]] || \
    fail "current api and worker image pins differ"

container_source_env() {
    docker inspect "$1" --format '{{range .Config.Env}}{{println .}}{{end}}' 2>/dev/null |
        sed -n 's/^NUBARCA_GIT_SHA=//p' | tail -n 1
}
CURRENT_API_SHA="$(container_source_env nubarca-api)"
CURRENT_FRONTEND_SHA="$(docker inspect nubarca-frontend \
    --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' 2>/dev/null || true)"
[[ "$CURRENT_API_SHA" =~ ^[0-9a-f]{40}$ ]] || fail "running API has no valid NUBARCA_GIT_SHA"
[[ "$CURRENT_FRONTEND_SHA" =~ ^[0-9a-f]{40}$ ]] || fail "running frontend has no valid revision label"
git cat-file -e "$CURRENT_API_SHA^{commit}" 2>/dev/null || fail "running API source is absent from Git history"
git cat-file -e "$CURRENT_FRONTEND_SHA^{commit}" 2>/dev/null || fail "running frontend source is absent from Git history"

changed_between() {
    local from="$1"
    shift
    [[ "$from" != "$CANDIDATE_SHA" ]] && ! git diff --quiet "$from..$CANDIDATE_SHA" -- "$@"
}

BACKEND_CHANGED=false
FRONTEND_CHANGED=false
TV_CHANGED=false
changed_between "$CURRENT_API_SHA" \
    src/NubArca.Api docker-compose.prod.yml docker-compose.facedirect-api.yml \
    scripts/verify-production-image.sh && BACKEND_CHANGED=true
changed_between "$CURRENT_FRONTEND_SHA" \
    frontend scripts/verify-production-frontend-image.sh && FRONTEND_CHANGED=true

TV_SOURCE_FILE=""
TV_APK_CURRENT_SHA=""
if [[ -n "${NUBARCA_TV_APK_DIR:-}" ]]; then
    TV_SOURCE_FILE="${NUBARCA_TV_APK_DIR%/}/.nubarca-tv.source"
elif grep -qE '^[[:space:]]*(export[[:space:]]+)?NUBARCA_TV_APK_DIR=' "$ENV_FILE"; then
    TV_APK_DIR_LINE="$(sed -n -E 's/^[[:space:]]*(export[[:space:]]+)?NUBARCA_TV_APK_DIR=//p' "$ENV_FILE" | tail -n 1)"
    TV_APK_DIR_LINE="${TV_APK_DIR_LINE%\"}"; TV_APK_DIR_LINE="${TV_APK_DIR_LINE#\"}"
    TV_APK_DIR_LINE="${TV_APK_DIR_LINE%\'}"; TV_APK_DIR_LINE="${TV_APK_DIR_LINE#\'}"
    [[ "$TV_APK_DIR_LINE" == /* && "$TV_APK_DIR_LINE" != "/" ]] || fail "invalid NUBARCA_TV_APK_DIR"
    TV_SOURCE_FILE="${TV_APK_DIR_LINE%/}/.nubarca-tv.source"
fi
if [[ -n "$TV_SOURCE_FILE" && -f "$TV_SOURCE_FILE" ]]; then
    TV_APK_CURRENT_SHA="$(tr -d '\r\n' < "$TV_SOURCE_FILE")"
fi
if [[ "$TV_APK_CURRENT_SHA" =~ ^[0-9a-f]{40}$ ]]; then
    git cat-file -e "$TV_APK_CURRENT_SHA^{commit}" 2>/dev/null || TV_APK_CURRENT_SHA=""
fi

TV_OTA_STORAGE_ROOT=""
if grep -qE '^[[:space:]]*(export[[:space:]]+)?NUBARCA_TV_OTA_STORAGE_ROOT=' "$ENV_FILE"; then
    TV_OTA_STORAGE_ROOT="$(sed -n -E 's/^[[:space:]]*(export[[:space:]]+)?NUBARCA_TV_OTA_STORAGE_ROOT=//p' "$ENV_FILE" | tail -n 1)"
    TV_OTA_STORAGE_ROOT="${TV_OTA_STORAGE_ROOT%\"}"; TV_OTA_STORAGE_ROOT="${TV_OTA_STORAGE_ROOT#\"}"
    TV_OTA_STORAGE_ROOT="${TV_OTA_STORAGE_ROOT%\'}"; TV_OTA_STORAGE_ROOT="${TV_OTA_STORAGE_ROOT#\'}"
    [[ "$TV_OTA_STORAGE_ROOT" == /* && "$TV_OTA_STORAGE_ROOT" != "/" ]] || fail "invalid NUBARCA_TV_OTA_STORAGE_ROOT"
fi
TV_OTA_CURRENT_SHA=""
if [[ -n "$TV_OTA_STORAGE_ROOT" && -f "$TV_OTA_STORAGE_ROOT/.nubarca-tv-ota.source" ]]; then
    TV_OTA_CURRENT_SHA="$(tr -d '\r\n' < "$TV_OTA_STORAGE_ROOT/.nubarca-tv-ota.source")"
fi
if [[ "$TV_OTA_CURRENT_SHA" =~ ^[0-9a-f]{40}$ ]]; then
    git cat-file -e "$TV_OTA_CURRENT_SHA^{commit}" 2>/dev/null || TV_OTA_CURRENT_SHA=""
fi

TV_CURRENT_SHA=""
TV_DISTANCE=""
for published_sha in "$TV_APK_CURRENT_SHA" "$TV_OTA_CURRENT_SHA"; do
    [[ "$published_sha" =~ ^[0-9a-f]{40}$ ]] || continue
    git merge-base --is-ancestor "$published_sha" "$CANDIDATE_SHA" || continue
    distance="$(git rev-list --count "$published_sha..$CANDIDATE_SHA")"
    if [[ -z "$TV_DISTANCE" || "$distance" -lt "$TV_DISTANCE" ]]; then
        TV_CURRENT_SHA="$published_sha"
        TV_DISTANCE="$distance"
    fi
done
if [[ -n "$TV_CURRENT_SHA" ]]; then
    changed_between "$TV_CURRENT_SHA" tv deploy/validate-tv-apk.sh deploy/pull-publish-tv-ota-image.sh \
        .github/workflows/tv-native-release.yml .github/workflows/tv-ota-release.yml && TV_CHANGED=true
else
    # A legacy publication has no source marker. Offer TV only when CI has
    # published a bundle for this exact candidate; never guess an older build.
    TV_CHANGED=true
fi

MIGRATION_PLAN=""
if ! MIGRATION_PLAN="$(python3 deploy/production-migration-plan.py \
    "$CURRENT_API_SHA" "$CANDIDATE_SHA")"; then
    fail "candidate migration set is not approved for automatic deployment; follow deploy/FAST_DEPLOY.md"
fi
MIGRATION_IDS=()
if [[ -n "$MIGRATION_PLAN" ]]; then
    mapfile -t MIGRATION_IDS <<< "$MIGRATION_PLAN"
    load_migration_backup_preflight
fi

manifest_digest() {
    local tag="$1" manifest
    manifest="$(docker manifest inspect --verbose "$tag" 2>/dev/null)" || return 1
    python3 -c 'import json,sys
d=json.load(sys.stdin)
digest=(d.get("Descriptor") or d.get("descriptor") or {}).get("digest")
if not isinstance(digest,str) or not digest.startswith("sha256:"): raise SystemExit(1)
print(digest)' <<< "$manifest"
}

API_REPO="ghcr.io/$GHCR_OWNER/nubarca-api-openvino"
FRONTEND_REPO="ghcr.io/$GHCR_OWNER/nubarca-frontend"
TV_REPO="ghcr.io/$GHCR_OWNER/nubarca-tv-apk"
TV_OTA_REPO="ghcr.io/$GHCR_OWNER/nubarca-tv-ota"
API_DIGEST=""; FRONTEND_DIGEST=""; TV_DIGEST=""; TV_OTA_DIGEST=""
if [[ "$BACKEND_CHANGED" == true ]]; then
    API_DIGEST="$(manifest_digest "$API_REPO:$CANDIDATE_SHA" || true)"
fi
if [[ "$FRONTEND_CHANGED" == true ]]; then
    FRONTEND_DIGEST="$(manifest_digest "$FRONTEND_REPO:$CANDIDATE_SHA" || true)"
fi
if [[ "$TV_CHANGED" == true ]]; then
    TV_DIGEST="$(manifest_digest "$TV_REPO:$CANDIDATE_SHA" || true)"
    TV_OTA_DIGEST="$(manifest_digest "$TV_OTA_REPO:$CANDIDATE_SHA" || true)"
fi

log "checkout:  $HEAD_SHA"
log "candidate: $CANDIDATE_SHA"
log "backend:   changed=$BACKEND_CHANGED artifact=$([[ -n "$API_DIGEST" ]] && echo ready || echo absent)"
log "frontend:  changed=$FRONTEND_CHANGED artifact=$([[ -n "$FRONTEND_DIGEST" ]] && echo ready || echo absent)"
log "TV native: changed=$TV_CHANGED artifact=$([[ -n "$TV_DIGEST" ]] && echo ready || echo absent)"
log "TV OTA:    changed=$TV_CHANGED artifact=$([[ -n "$TV_OTA_DIGEST" ]] && echo ready || echo absent)"
log "migration: $([[ ${#MIGRATION_IDS[@]} -gt 0 ]] && echo "approved (${MIGRATION_IDS[*]})" || echo none)"
if [[ ${#MIGRATION_IDS[@]} -gt 0 ]]; then
    log "backup:    ready root=$BACKUP_ROOT database-bytes=$DATABASE_SIZE_BYTES available-kb=$BACKUP_AVAILABLE_KB"
fi

if [[ "$MODE" == "check" ]]; then
    if [[ "$BACKEND_CHANGED" == true && -z "$API_DIGEST" ]] || \
       [[ "$FRONTEND_CHANGED" == true && -z "$FRONTEND_DIGEST" ]]; then
        log "not deployable yet: required CI application image is missing"
        exit 3
    fi
    if [[ "$TV_CHANGED" == true && -z "$TV_DIGEST" && -z "$TV_OTA_DIGEST" ]]; then
        log "not deployable yet: no CI-built TV native or OTA artifact exists for the candidate"
        exit 3
    fi
    log "ready. After reviewing the SHA, run:"
    printf './deploy/update-production.sh apply --env-file %q --confirm %s' \
        "$ENV_FILE" "$CANDIDATE_SHA"
    if [[ ${#MIGRATION_IDS[@]} -gt 0 ]]; then
        printf ' --confirm-migrations'
    fi
    printf '\n'
    exit 0
fi

[[ ${#MIGRATION_IDS[@]} -eq 0 || "$CONFIRM_MIGRATIONS" == true ]] || \
    fail "candidate contains approved migrations; run check and repeat its --confirm-migrations command"
[[ ${#MIGRATION_IDS[@]} -gt 0 || "$CONFIRM_MIGRATIONS" == false ]] || \
    fail "--confirm-migrations was supplied but the candidate contains no migrations; run check again"
[[ "$BACKEND_CHANGED" != true || -n "$API_DIGEST" ]] || fail "required backend image is not published"
[[ "$FRONTEND_CHANGED" != true || -n "$FRONTEND_DIGEST" ]] || fail "required frontend image is not published"
[[ "$TV_CHANGED" != true || -n "$TV_DIGEST" || -n "$TV_OTA_DIGEST" ]] || \
    fail "required TV native or OTA artifact is not published"

root_available_kb="$(df -Pk / | awk 'NR==2 {print $4}')"
root_used_percent="$(df -Pk / | awk 'NR==2 {gsub(/%/,"",$5); print $5}')"
[[ "$root_used_percent" -lt 90 && "$root_available_kb" -ge 10485760 ]] || \
    fail "root filesystem fails the production capacity gate"

if [[ "$HEAD_SHA" != "$CANDIDATE_SHA" ]]; then
    git merge --ff-only origin/main
fi
[[ "$(git rev-parse HEAD)" == "$CANDIDATE_SHA" ]] || fail "checkout did not reach confirmed SHA"

NEW_API_IMAGE="$CURRENT_API_IMAGE"
NEW_FRONTEND_IMAGE="$CURRENT_FRONTEND_IMAGE"
AFFECTED=()
if [[ "$BACKEND_CHANGED" == true ]]; then
    NEW_API_IMAGE="$API_REPO@$API_DIGEST"
    docker pull "$NEW_API_IMAGE"
    scripts/verify-production-image.sh "$NEW_API_IMAGE" "$CANDIDATE_SHA" openvino
    AFFECTED+=(api worker)
fi
if [[ "$FRONTEND_CHANGED" == true ]]; then
    NEW_FRONTEND_IMAGE="$FRONTEND_REPO@$FRONTEND_DIGEST"
    docker pull "$NEW_FRONTEND_IMAGE"
    scripts/verify-production-frontend-image.sh "$NEW_FRONTEND_IMAGE" "$CANDIDATE_SHA"
    AFFECTED+=(frontend)
fi

pin_tmp="$(mktemp /tmp/nubarca-release-pin.XXXXXXXX.yml)"
pin_previous="$(mktemp /tmp/nubarca-release-previous.XXXXXXXX.yml)"
cp docker-compose.release.local.yml "$pin_previous"
cleanup() { rm -f -- "$pin_tmp" "$pin_previous"; }
trap cleanup EXIT
printf 'services:\n  api:\n    image: "%s"\n  worker:\n    image: "%s"\n  frontend:\n    image: "%s"\n' \
    "$NEW_API_IMAGE" "$NEW_API_IMAGE" "$NEW_FRONTEND_IMAGE" > "$pin_tmp"

COMPOSE_CANDIDATE=(
    docker compose
    -f docker-compose.prod.yml
    -f docker-compose.prod.local.yml
    -f docker-compose.facedirect-api.yml
    -f "$pin_tmp"
    --env-file "$ENV_FILE"
)
"${COMPOSE_CANDIDATE[@]}" --profile worker config --format json |
    python3 -c 'import json,sys
s=json.load(sys.stdin)["services"]
api,frontend=sys.argv[1:]
assert s["api"]["image"]==api and s["worker"]["image"]==api
assert s["frontend"]["image"]==frontend
for name in ("api","worker","frontend"): assert "build" not in s[name]
for name in ("api","worker"):
    assert "/dev/dri" in json.dumps(s[name].get("devices",[]))
    assert s[name].get("group_add")
' "$NEW_API_IMAGE" "$NEW_FRONTEND_IMAGE"

if [[ ${#MIGRATION_IDS[@]} -gt 0 ]]; then
    create_migration_backup
    run_candidate_migrations
fi

if [[ ${#AFFECTED[@]} -gt 0 ]]; then
    history_dir="$REPO_ROOT/deploy-history"
    install -d -m 0700 "$history_dir"
    stamp="$(date -u +%Y%m%dT%H%M%SZ)"
    install -m 0600 docker-compose.release.local.yml "$history_dir/release-pin-before-$stamp.yml"
    if [[ -n "$MIGRATION_BACKUP" ]]; then
        printf 'source_sha=%s\nbackup=%s\nbackup_sha256=%s\nmigrations=%s\n' \
            "$CANDIDATE_SHA" "$MIGRATION_BACKUP" "$MIGRATION_BACKUP_SHA" "${MIGRATION_IDS[*]}" \
            > "$history_dir/migration-$stamp.txt"
        chmod 0600 "$history_dir/migration-$stamp.txt"
    fi
    install -m 0644 "$pin_tmp" docker-compose.release.local.yml.tmp
    mv -f docker-compose.release.local.yml.tmp docker-compose.release.local.yml

    rollback_needed=true
    rollback() {
        if [[ "$rollback_needed" == true ]]; then
            log "smoke check failed; restoring the previous image pins"
            if [[ -n "$MIGRATION_BACKUP" ]]; then
                log "approved migrations remain applied and are compatible with the previous application image"
                log "verified pre-migration backup remains at $MIGRATION_BACKUP"
            fi
            install -m 0644 "$pin_previous" docker-compose.release.local.yml
            "${COMPOSE_BASE[@]}" up -d --no-build --no-deps "${AFFECTED[@]}" || true
        fi
        cleanup
    }
    trap rollback EXIT
    "${COMPOSE_BASE[@]}" up -d --no-build --no-deps "${AFFECTED[@]}"

    healthy=false
    for _ in $(seq 1 60); do
        if curl -fsS --max-time 3 http://127.0.0.1:8080/health/ready >/dev/null && \
           curl -fsS --max-time 3 http://127.0.0.1:8081/ >/dev/null; then
            healthy=true
            break
        fi
        sleep 2
    done
    [[ "$healthy" == true ]] || fail "application smoke checks did not become healthy"
    [[ "$BACKEND_CHANGED" != true || "$(container_source_env nubarca-api)" == "$CANDIDATE_SHA" ]] || \
        fail "running API provenance differs from the confirmed SHA"
    if [[ "$FRONTEND_CHANGED" == true ]]; then
        running_frontend_sha="$(docker inspect nubarca-frontend --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')"
        [[ "$running_frontend_sha" == "$CANDIDATE_SHA" ]] || fail "running frontend provenance differs from the confirmed SHA"
    fi
    rollback_needed=false
    trap cleanup EXIT
fi

if [[ -n "$TV_DIGEST" ]]; then
    deploy/pull-publish-tv-apk-image.sh --env-file "$ENV_FILE" "$TV_REPO@$TV_DIGEST"
fi
if [[ -n "$TV_OTA_DIGEST" ]]; then
    deploy/pull-publish-tv-ota-image.sh --env-file "$ENV_FILE" "$TV_OTA_REPO@$TV_OTA_DIGEST"
fi

log "production update complete at $CANDIDATE_SHA"
log "physical Fire Stick acceptance remains pending after a TV publication"

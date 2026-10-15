#!/usr/bin/env bash
# Check for, or apply, one CI-built NubArca release from the server.
# This script never builds and never prunes. It installs only what a release
# manifest names whose Sigstore signature proves it came from NubArca's own
# Release workflow on main (deploy/release-manifest.py). Reviewed additive
# migrations run only after an explicit confirmation and a verified
# pre-migration backup.
#
# It is what the host updater (deploy/updater/nubarca-updater.py) runs when an
# administrator presses "Check" or "Install" on the Updates page, and what an
# operator runs by hand: both get the same gates. --report-json writes a
# machine-readable outcome for the updater; nothing in it names a path,
# credential or digest-free image.

set -Eeuo pipefail

REPORT_JSON=""
MODE=""

log() { printf '[production-update] %s\n' "$*"; }

# outcome: failed (a gate refused or a step broke), with the reason in words.
write_failure_report() {
    [[ -n "$REPORT_JSON" ]] || return 0
    REPORT_MODE="$MODE" REPORT_MESSAGE="$1" python3 - "$REPORT_JSON" <<'PY' || true
import datetime, json, os, sys
report = {
    "schemaVersion": 1,
    "mode": os.environ.get("REPORT_MODE") or None,
    "outcome": "failed",
    "message": os.environ.get("REPORT_MESSAGE", "")[:500],
    "finishedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
}
path = sys.argv[1]
with open(path + ".tmp", "w", encoding="utf-8") as handle:
    json.dump(report, handle, indent=2)
os.replace(path + ".tmp", path)
PY
}

fail() {
    printf '[production-update] error: %s\n' "$*" >&2
    write_failure_report "$*"
    exit 1
}

usage() {
    cat >&2 <<'EOF'
usage:
  ./deploy/update-production.sh check --env-file <production.env> [--report-json <file>]
  ./deploy/update-production.sh apply --env-file <production.env> --confirm <full-main-sha> [--confirm-migrations] [--report-json <file>]
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
        --report-json) [[ $# -ge 2 ]] || usage; REPORT_JSON="$2"; shift 2 ;;
        *) usage ;;
    esac
done
[[ -n "$ENV_FILE" ]] || fail "--env-file is required"
[[ "$MODE" == "check" && -z "$CONFIRM_SHA" && "$CONFIRM_MIGRATIONS" == false ]] || \
    [[ "$MODE" == "apply" && "$CONFIRM_SHA" =~ ^[0-9a-f]{40}$ ]] || usage

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"
[[ -f "$ENV_FILE" ]] || fail "environment file not found"
for command in git docker python3 curl gzip sha256sum flock cosign; do
    command -v "$command" >/dev/null || fail "$command is required (cosign: deploy/updater/install-updater.sh installs the pinned version)"
done
[[ "$(cosign version 2>/dev/null | sed -n 's/^GitVersion:[[:space:]]*//p')" == v2.* ]] || \
    fail "cosign 2.x is required to verify release signatures"
[[ "$(git branch --show-current)" == "main" ]] || fail "production checkout must be on main"
[[ -z "$(git status --porcelain)" ]] || fail "production checkout is dirty"

exec 9>/tmp/nubarca-production-update.lock
flock -n 9 || fail "another production update is already running"

read_env_value() {
    local key="$1" value
    value="$(sed -n -E "s/^[[:space:]]*(export[[:space:]]+)?${key}=//p" "$ENV_FILE" | tail -n 1)"
    value="${value%\"}"; value="${value#\"}"
    value="${value%\'}"; value="${value#\'}"
    printf '%s' "$value"
}

# What this installation runs. Both are plain choices, never locations:
#   NUBARCA_API_VARIANT  openvino (default: Intel GPU, four-file stack) | runtime (lean, CPU only)
#   NUBARCA_TV_ENABLED   true (default) | false — false skips TV publication on purpose
API_VARIANT="$(read_env_value NUBARCA_API_VARIANT)"
API_VARIANT="${API_VARIANT:-openvino}"
[[ "$API_VARIANT" == "openvino" || "$API_VARIANT" == "runtime" ]] || \
    fail "NUBARCA_API_VARIANT must be openvino or runtime"
TV_ENABLED="$(read_env_value NUBARCA_TV_ENABLED)"
TV_ENABLED="${TV_ENABLED:-true}"
[[ "$TV_ENABLED" == "true" || "$TV_ENABLED" == "false" ]] || fail "NUBARCA_TV_ENABLED must be true or false"

# Trust is fixed in deploy/release-manifest.py, never derived from the remote.
GHCR_OWNER="$(python3 deploy/release-manifest.py trust owner)"
RELEASE_IMAGE="$(python3 deploy/release-manifest.py trust release-image)"
TRUSTED_REPOSITORY="$(python3 deploy/release-manifest.py trust repository)"
remote="$(git remote get-url origin)"
[[ "$remote" =~ github\.com[:/]([^/]+/[^/]+)$ ]] || fail "origin must be a GitHub repository"
remote_repository="${BASH_REMATCH[1]%.git}"
[[ "${remote_repository,,}" == "${TRUSTED_REPOSITORY,,}" ]] || \
    fail "origin is not $TRUSTED_REPOSITORY; this updater installs only NubArca's own releases"

log "fetching origin/main"
git fetch --quiet origin main
CANDIDATE_SHA="$(git rev-parse origin/main)"
HEAD_SHA="$(git rev-parse HEAD)"
[[ "$MODE" != "apply" || "$CONFIRM_SHA" == "$CANDIDATE_SHA" ]] || \
    fail "origin/main moved: confirmed $CONFIRM_SHA, now $CANDIDATE_SHA; run check again"
git merge-base --is-ancestor "$HEAD_SHA" "$CANDIDATE_SHA" || \
    fail "the checkout is not an ancestor of origin/main; it cannot fast-forward"

COMPOSE_FILES=(-f docker-compose.prod.yml -f docker-compose.prod.local.yml)
[[ "$API_VARIANT" == "openvino" ]] && COMPOSE_FILES+=(-f docker-compose.facedirect-api.yml)
COMPOSE_BASE=(docker compose "${COMPOSE_FILES[@]}" -f docker-compose.release.local.yml --env-file "$ENV_FILE")
for file in docker-compose.prod.yml docker-compose.prod.local.yml docker-compose.release.local.yml; do
    [[ -f "$file" ]] || fail "required production file is missing: $file"
done

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
        fail "BACKUP_DIR must already exist and be writable"

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
        fail "pre-migration backup target already exists"
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

component_paths() {
    mapfile -t COMPONENT_PATHS < <(python3 deploy/release-manifest.py paths "$REPO_ROOT" "$CANDIDATE_SHA" "$1") || \
        fail "cannot read the paths of component $1 at the candidate"
    # An empty list would make git diff compare the whole tree.
    [[ ${#COMPONENT_PATHS[@]} -gt 0 && -n "${COMPONENT_PATHS[0]}" ]] || \
        fail "cannot read the paths of component $1 at the candidate"
}

# ---- The signed release manifest -------------------------------------------------
NOT_DEPLOYABLE_REASON=""
RELEASE_DIR="$(mktemp -d /tmp/nubarca-release.XXXXXXXX)"
cleanup_release() {
    if [[ "$RELEASE_DIR" == /tmp/nubarca-release.* && -d "$RELEASE_DIR" ]]; then rm -rf -- "$RELEASE_DIR"; fi
}
trap cleanup_release EXIT

API_SOURCE=""; FRONTEND_SOURCE=""; PRINT_AGENT_SOURCE=""; PRINT_AGENT_VERSION=""; RELEASE_VERSION=""
API_REPO=""; API_DIGEST=""; FRONTEND_REPO=""; FRONTEND_DIGEST=""; PRINT_AGENT_IMAGE=""
if ! docker manifest inspect "$RELEASE_IMAGE:$CANDIDATE_SHA" >/dev/null 2>&1; then
    NOT_DEPLOYABLE_REASON="release-not-published"
else
    # The signature, then the content: the manifest is believed only once its
    # signature is proved from the signer THIS checkout (the release already
    # installed) holds. A new signer is accepted only through releases the
    # previous one also signed (signed trust transitions), never because the
    # candidate says so.
    signature_note="$(python3 deploy/release-manifest.py fetch-verified "$CANDIDATE_SHA" "$REPO_ROOT" "$RELEASE_DIR")" || \
        fail "the release manifest signature does not verify; nothing from it is installed"
    log "release manifest $signature_note"
    release_lines="$(python3 deploy/release-manifest.py verify "$RELEASE_DIR/release.json" "$CANDIDATE_SHA" "$REPO_ROOT")" || \
        fail "the release manifest does not describe the candidate"
    RELEASE_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["version"])' "$RELEASE_DIR/release.json")"
    while IFS=$'\t' read -r component variant image source version; do
        case "$component/$variant" in
            "api/$API_VARIANT") API_REPO="${image%@*}"; API_DIGEST="${image#*@}"; API_SOURCE="$source" ;;
            frontend/default) FRONTEND_REPO="${image%@*}"; FRONTEND_DIGEST="${image#*@}"; FRONTEND_SOURCE="$source" ;;
            print-agent/linux-x64) PRINT_AGENT_IMAGE="$image"; PRINT_AGENT_SOURCE="$source"; PRINT_AGENT_VERSION="$version" ;;
        esac
    done <<< "$release_lines"
    [[ -n "$API_DIGEST" && -n "$FRONTEND_DIGEST" && -n "$PRINT_AGENT_IMAGE" ]] || \
        fail "the release manifest lacks an image this installation needs"
fi

BACKEND_CHANGED=false
FRONTEND_CHANGED=false
TV_CHANGED=false
PRINT_AGENT_CHANGED=false
component_paths api
changed_between "$CURRENT_API_SHA" "${COMPONENT_PATHS[@]}" && BACKEND_CHANGED=true
component_paths frontend
changed_between "$CURRENT_FRONTEND_SHA" "${COMPONENT_PATHS[@]}" && FRONTEND_CHANGED=true

# The Print Box bundle this server hands out, when the API mounts its volume.
PRINT_AGENT_MOUNTED=false
PRINT_AGENT_CURRENT_SHA=""
if docker inspect nubarca-api --format '{{range .Mounts}}{{.Name}} {{end}}' 2>/dev/null |
    tr ' ' '\n' | grep -qx nubarca-print-agent-data; then
    PRINT_AGENT_MOUNTED=true
    PRINT_AGENT_CURRENT_SHA="$(docker exec nubarca-api cat /var/lib/nubarca/print-agent/current-linux-x64.json 2>/dev/null |
        python3 -c 'import json,sys
try: print(json.load(sys.stdin).get("sourceCommit",""))
except Exception: print("")' || true)"
fi
if [[ "$PRINT_AGENT_MOUNTED" == true && -n "$PRINT_AGENT_SOURCE" && "$PRINT_AGENT_CURRENT_SHA" != "$PRINT_AGENT_SOURCE" ]]; then
    component_paths print-agent
    if [[ "$PRINT_AGENT_CURRENT_SHA" =~ ^[0-9a-f]{40}$ ]] && \
        git cat-file -e "$PRINT_AGENT_CURRENT_SHA^{commit}" 2>/dev/null; then
        changed_between "$PRINT_AGENT_CURRENT_SHA" "${COMPONENT_PATHS[@]}" && PRINT_AGENT_CHANGED=true
    else
        PRINT_AGENT_CHANGED=true
    fi
fi

TV_SOURCE_FILE=""
TV_APK_CURRENT_SHA=""
if [[ -n "${NUBARCA_TV_APK_DIR:-}" ]]; then
    TV_SOURCE_FILE="${NUBARCA_TV_APK_DIR%/}/.nubarca-tv.source"
elif grep -qE '^[[:space:]]*(export[[:space:]]+)?NUBARCA_TV_APK_DIR=' "$ENV_FILE"; then
    TV_APK_DIR_LINE="$(read_env_value NUBARCA_TV_APK_DIR)"
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
    TV_OTA_STORAGE_ROOT="$(read_env_value NUBARCA_TV_OTA_STORAGE_ROOT)"
    [[ "$TV_OTA_STORAGE_ROOT" == /* && "$TV_OTA_STORAGE_ROOT" != "/" ]] || fail "invalid NUBARCA_TV_OTA_STORAGE_ROOT"
fi
TV_OTA_CURRENT_SHA=""
if [[ -n "$TV_OTA_STORAGE_ROOT" && -f "$TV_OTA_STORAGE_ROOT/.nubarca-tv-ota.source" ]]; then
    TV_OTA_CURRENT_SHA="$(tr -d '\r\n' < "$TV_OTA_STORAGE_ROOT/.nubarca-tv-ota.source")"
fi
if [[ "$TV_OTA_CURRENT_SHA" =~ ^[0-9a-f]{40}$ ]]; then
    git cat-file -e "$TV_OTA_CURRENT_SHA^{commit}" 2>/dev/null || TV_OTA_CURRENT_SHA=""
fi

if [[ "$TV_ENABLED" == true ]]; then
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
else
    log "TV publication is switched off for this installation (NUBARCA_TV_ENABLED=false)"
fi

MIGRATION_PLAN=""
MIGRATION_IDS=()
if ! MIGRATION_PLAN="$(python3 deploy/production-migration-plan.py \
    "$CURRENT_API_SHA" "$CANDIDATE_SHA")"; then
    [[ -n "$NOT_DEPLOYABLE_REASON" ]] || NOT_DEPLOYABLE_REASON="manual-migration"
fi
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

TV_REPO="ghcr.io/$GHCR_OWNER/nubarca-tv-apk"
TV_OTA_REPO="ghcr.io/$GHCR_OWNER/nubarca-tv-ota"
TV_DIGEST=""; TV_OTA_DIGEST=""
if [[ "$TV_CHANGED" == true ]]; then
    TV_DIGEST="$(manifest_digest "$TV_REPO:$CANDIDATE_SHA" || true)"
    TV_OTA_DIGEST="$(manifest_digest "$TV_OTA_REPO:$CANDIDATE_SHA" || true)"
    [[ -n "$TV_DIGEST" || -n "$TV_OTA_DIGEST" || -n "$NOT_DEPLOYABLE_REASON" ]] || \
        NOT_DEPLOYABLE_REASON="tv-artifact-missing"
fi

UP_TO_DATE=false
if [[ -z "$NOT_DEPLOYABLE_REASON" && "$HEAD_SHA" == "$CANDIDATE_SHA" && "$BACKEND_CHANGED" == false && \
      "$FRONTEND_CHANGED" == false && "$TV_CHANGED" == false && "$PRINT_AGENT_CHANGED" == false ]]; then
    UP_TO_DATE=true
fi

version_at() {
    git show "$1:src/NubArca.Api/NubArca.Api.csproj" 2>/dev/null | sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' | head -n 1
}
CURRENT_VERSION="$(version_at "$CURRENT_API_SHA")"
CANDIDATE_VERSION="${RELEASE_VERSION:-$(version_at "$CANDIDATE_SHA")}"

log "checkout:  $HEAD_SHA"
log "running:   api=$CURRENT_API_SHA frontend=$CURRENT_FRONTEND_SHA variant=$API_VARIANT"
log "candidate: $CANDIDATE_SHA (release ${CANDIDATE_VERSION:-unknown})"
log "release:   $([[ "$NOT_DEPLOYABLE_REASON" == release-not-published ]] && echo "not published yet" || echo "signed manifest verified")"
log "backend:   changed=$BACKEND_CHANGED source=${API_SOURCE:-none}"
log "frontend:  changed=$FRONTEND_CHANGED source=${FRONTEND_SOURCE:-none}"
log "print box: changed=$PRINT_AGENT_CHANGED version=${PRINT_AGENT_VERSION:-none} served=$PRINT_AGENT_MOUNTED"
log "TV native: changed=$TV_CHANGED artifact=$([[ -n "$TV_DIGEST" ]] && echo ready || echo absent)"
log "TV OTA:    changed=$TV_CHANGED artifact=$([[ -n "$TV_OTA_DIGEST" ]] && echo ready || echo absent)"
log "migration: $([[ ${#MIGRATION_IDS[@]} -gt 0 ]] && echo "approved (${MIGRATION_IDS[*]})" || \
    { [[ "$NOT_DEPLOYABLE_REASON" == manual-migration ]] && echo "needs the manual path" || echo none; })"
if [[ ${#MIGRATION_IDS[@]} -gt 0 ]]; then
    log "backup:    ready database-bytes=$DATABASE_SIZE_BYTES available-kb=$BACKUP_AVAILABLE_KB"
fi

# outcome: ready | up-to-date | not-deployable (check), applied (apply).
write_report() {
    [[ -n "$REPORT_JSON" ]] || return 0
    local outcome="$1" commits
    commits="$(git log --no-merges --max-count=50 --format='%h%x1f%s' "$CURRENT_API_SHA..$CANDIDATE_SHA" 2>/dev/null || true)"
    R_OUTCOME="$outcome" R_MODE="$MODE" R_REASON="$NOT_DEPLOYABLE_REASON" R_CANDIDATE="$CANDIDATE_SHA" \
    R_CANDIDATE_VERSION="$CANDIDATE_VERSION" R_CURRENT_API="$CURRENT_API_SHA" R_CURRENT_FRONTEND="$CURRENT_FRONTEND_SHA" \
    R_CURRENT_VERSION="$CURRENT_VERSION" R_CHECKOUT="$HEAD_SHA" R_VARIANT="$API_VARIANT" \
    R_BACKEND="$BACKEND_CHANGED" R_FRONTEND="$FRONTEND_CHANGED" R_TV="$TV_CHANGED" R_TV_ENABLED="$TV_ENABLED" \
    R_TV_READY="$([[ -n "$TV_DIGEST$TV_OTA_DIGEST" ]] && echo true || echo false)" \
    R_PRINT_AGENT="$PRINT_AGENT_CHANGED" R_PRINT_AGENT_VERSION="$PRINT_AGENT_VERSION" R_PRINT_AGENT_SERVED="$PRINT_AGENT_MOUNTED" \
    R_SIGNED="$([[ -n "$API_DIGEST" ]] && echo true || echo false)" \
    R_MIGRATIONS="${MIGRATION_IDS[*]}" R_COMMITS="$commits" \
    python3 - "$REPORT_JSON" <<'PY'
import datetime, json, os, sys
e = os.environ
flag = lambda name: e.get(name) == "true"
report = {
    "schemaVersion": 1,
    "mode": e["R_MODE"],
    "outcome": e["R_OUTCOME"],
    "reason": e.get("R_REASON") or None,
    "finishedAt": datetime.datetime.now(datetime.timezone.utc).isoformat(timespec="seconds"),
    "candidate": {"sha": e["R_CANDIDATE"], "version": e.get("R_CANDIDATE_VERSION") or None,
                  "signed": flag("R_SIGNED")},
    "current": {"apiSha": e["R_CURRENT_API"], "frontendSha": e["R_CURRENT_FRONTEND"],
                "checkoutSha": e["R_CHECKOUT"], "version": e.get("R_CURRENT_VERSION") or None,
                "apiVariant": e["R_VARIANT"]},
    "components": {
        "backend": {"changed": flag("R_BACKEND")},
        "frontend": {"changed": flag("R_FRONTEND")},
        "printAgent": {"changed": flag("R_PRINT_AGENT"), "version": e.get("R_PRINT_AGENT_VERSION") or None,
                       "served": flag("R_PRINT_AGENT_SERVED")},
        "tv": {"changed": flag("R_TV"), "enabled": flag("R_TV_ENABLED"), "ready": flag("R_TV_READY")},
    },
    "migrations": [m for m in e.get("R_MIGRATIONS", "").split() if m],
    "confirmMigrations": bool(e.get("R_MIGRATIONS", "").split()),
    "commits": [
        {"sha": sha, "subject": subject[:200]}
        for sha, _, subject in (line.partition("\x1f") for line in e.get("R_COMMITS", "").splitlines())
        if sha
    ],
}
path = sys.argv[1]
with open(path + ".tmp", "w", encoding="utf-8") as handle:
    json.dump(report, handle, indent=2)
os.replace(path + ".tmp", path)
PY
}

if [[ "$MODE" == "check" ]]; then
    if [[ -n "$NOT_DEPLOYABLE_REASON" ]]; then
        case "$NOT_DEPLOYABLE_REASON" in
            release-not-published) log "not deployable yet: CI has not published a signed release for the candidate" ;;
            manual-migration) log "not deployable here: the candidate migration set is not approved for automatic deployment; follow deploy/FAST_DEPLOY.md" ;;
            tv-artifact-missing) log "not deployable yet: no CI-built TV native or OTA artifact exists for the candidate" ;;
        esac
        write_report not-deployable
        exit 3
    fi
    if [[ "$UP_TO_DATE" == true ]]; then
        log "up to date: this installation already runs the candidate"
        write_report up-to-date
        exit 0
    fi
    log "ready. After reviewing the SHA, run:"
    printf './deploy/update-production.sh apply --env-file %q --confirm %s' \
        "$ENV_FILE" "$CANDIDATE_SHA"
    if [[ ${#MIGRATION_IDS[@]} -gt 0 ]]; then
        printf ' --confirm-migrations'
    fi
    printf '\n'
    write_report ready
    exit 0
fi

case "$NOT_DEPLOYABLE_REASON" in
    "") ;;
    release-not-published) fail "CI has not published a signed release for the candidate; run check again later" ;;
    manual-migration) fail "candidate migration set is not approved for automatic deployment; follow deploy/FAST_DEPLOY.md" ;;
    tv-artifact-missing) fail "required TV native or OTA artifact is not published" ;;
    *) fail "candidate is not deployable: $NOT_DEPLOYABLE_REASON" ;;
esac
[[ ${#MIGRATION_IDS[@]} -eq 0 || "$CONFIRM_MIGRATIONS" == true ]] || \
    fail "candidate contains approved migrations; run check and repeat its --confirm-migrations command"
[[ ${#MIGRATION_IDS[@]} -gt 0 || "$CONFIRM_MIGRATIONS" == false ]] || \
    fail "--confirm-migrations was supplied but the candidate contains no migrations; run check again"

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
    scripts/verify-production-image.sh "$NEW_API_IMAGE" "$API_SOURCE" "$API_VARIANT"
    AFFECTED+=(api worker)
fi
if [[ "$FRONTEND_CHANGED" == true ]]; then
    NEW_FRONTEND_IMAGE="$FRONTEND_REPO@$FRONTEND_DIGEST"
    docker pull "$NEW_FRONTEND_IMAGE"
    scripts/verify-production-frontend-image.sh "$NEW_FRONTEND_IMAGE" "$FRONTEND_SOURCE"
    AFFECTED+=(frontend)
fi

pin_tmp="$(mktemp /tmp/nubarca-release-pin.XXXXXXXX.yml)"
pin_previous="$(mktemp /tmp/nubarca-release-previous.XXXXXXXX.yml)"
cp docker-compose.release.local.yml "$pin_previous"
cleanup() { rm -f -- "$pin_tmp" "$pin_previous"; cleanup_release; }
trap cleanup EXIT
printf 'services:\n  api:\n    image: "%s"\n  worker:\n    image: "%s"\n  frontend:\n    image: "%s"\n' \
    "$NEW_API_IMAGE" "$NEW_API_IMAGE" "$NEW_FRONTEND_IMAGE" > "$pin_tmp"

COMPOSE_CANDIDATE=(
    docker compose
    "${COMPOSE_FILES[@]}"
    -f "$pin_tmp"
    --env-file "$ENV_FILE"
)
"${COMPOSE_CANDIDATE[@]}" --profile worker config --format json |
    python3 -c 'import json,sys
s=json.load(sys.stdin)["services"]
api,frontend,variant=sys.argv[1:]
assert s["api"]["image"]==api and s["worker"]["image"]==api
assert s["frontend"]["image"]==frontend
for name in ("api","worker","frontend"): assert "build" not in s[name]
if variant=="openvino":
    for name in ("api","worker"):
        assert "/dev/dri" in json.dumps(s[name].get("devices",[]))
        assert s[name].get("group_add")
' "$NEW_API_IMAGE" "$NEW_FRONTEND_IMAGE" "$API_VARIANT" || fail "the candidate Compose model failed its gate"

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
    [[ "$healthy" == true ]] || fail "application smoke checks did not become healthy; the previous release was restored"
    [[ "$BACKEND_CHANGED" != true || "$(container_source_env nubarca-api)" == "$API_SOURCE" ]] || \
        fail "running API provenance differs from the release manifest; the previous release was restored"
    if [[ "$FRONTEND_CHANGED" == true ]]; then
        running_frontend_sha="$(docker inspect nubarca-frontend --format '{{index .Config.Labels "org.opencontainers.image.revision"}}')"
        [[ "$running_frontend_sha" == "$FRONTEND_SOURCE" ]] || \
            fail "running frontend provenance differs from the release manifest; the previous release was restored"
    fi
    rollback_needed=false
    trap cleanup EXIT
fi

# The Print Box bundle follows the server, never precedes it: the server
# serves every compatible older agent, so boxes update afterwards, each when
# an administrator asks (Updates page) and only while it is idle.
if [[ "$PRINT_AGENT_CHANGED" == true ]]; then
    deploy/pull-publish-print-agent-image.sh "$PRINT_AGENT_IMAGE" || \
        fail "the server was updated, but publishing Print Agent $PRINT_AGENT_VERSION for the boxes failed"
fi
if [[ -n "$TV_DIGEST" ]]; then
    deploy/pull-publish-tv-apk-image.sh --env-file "$ENV_FILE" "$TV_REPO@$TV_DIGEST"
fi
if [[ -n "$TV_OTA_DIGEST" ]]; then
    deploy/pull-publish-tv-ota-image.sh --env-file "$ENV_FILE" "$TV_OTA_REPO@$TV_OTA_DIGEST"
fi

write_report applied
log "production update complete at $CANDIDATE_SHA"
[[ -z "$TV_DIGEST$TV_OTA_DIGEST" ]] || log "physical Fire Stick acceptance remains pending after a TV publication"

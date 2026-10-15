#!/usr/bin/env bash
# Installs the NubArca host updater on one installation, from its production
# checkout, as root:
#
#   sudo ./deploy/updater/install-updater.sh --env-file .env --mailbox <directory>
#
# - installs the pinned cosign that verifies release signatures (checksum-checked);
# - creates the mailbox the api and the updater share, owned by the checkout's
#   owner, and records it in .env as NUBARCA_UPDATER_DIR;
# - installs and starts nubarca-updater.service, which runs as the checkout's
#   owner (the account that already runs deploy/update-production.sh), never as root.
#
# The mailbox is an installation choice, so it is an argument, never a default.
# Re-running it is safe: it converges on the same state. Removing the updater
# is `systemctl disable --now nubarca-updater`; nothing else depends on it.

set -Eeuo pipefail

log() { printf '[install-updater] %s\n' "$*"; }
fail() { printf '[install-updater] error: %s\n' "$*" >&2; exit 1; }

COSIGN_VERSION="v2.6.1"
declare -A COSIGN_SHA256=(
    [amd64]="064954c5d8c7e3b28188eee5b1727b31c411550bc5fefd41aa672d3c761d103a"
    [arm64]="56a16480bdd56ec789abaa65924402f6b92c0041f06885995853c05567b76f34"
)
UNIT="nubarca-updater.service"

ENV_FILE=""
MAILBOX=""
AUTO_CHECK_HOURS="6"
while [[ $# -gt 0 ]]; do
    case "$1" in
        --env-file) ENV_FILE="${2:-}"; shift 2 ;;
        --mailbox) MAILBOX="${2:-}"; shift 2 ;;
        --auto-check-hours) AUTO_CHECK_HOURS="${2:-}"; shift 2 ;;
        *) fail "usage: $0 --env-file <production.env> --mailbox <directory> [--auto-check-hours <n, 0 = never>]" ;;
    esac
done

[[ $EUID -eq 0 ]] || fail "run as root (sudo)"
[[ -n "$ENV_FILE" && -n "$MAILBOX" ]] || fail "--env-file and --mailbox are required"
[[ "$AUTO_CHECK_HOURS" =~ ^[0-9]{1,3}$ ]] || fail "--auto-check-hours must be a whole number of hours"
CHECKOUT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
ENV_FILE="$(cd "$(dirname "$ENV_FILE")" && pwd)/$(basename "$ENV_FILE")"
[[ -f "$ENV_FILE" ]] || fail "environment file not found: $ENV_FILE"
[[ "$MAILBOX" == /* && "$MAILBOX" != "/" ]] || fail "--mailbox must be an absolute directory, not /"
[[ "$MAILBOX" != "$CHECKOUT"* ]] || fail "the mailbox must live outside the checkout"
[[ "$MAILBOX" =~ ^/[A-Za-z0-9_./-]+$ ]] || fail "the mailbox path may use letters, digits, '.', '_', '-' and '/' only"
for command in python3 docker git systemctl sha256sum curl; do
    command -v "$command" >/dev/null || fail "$command is required"
done

OWNER="$(stat -c %U "$CHECKOUT")"
[[ "$OWNER" != "root" ]] || fail "the checkout is owned by root; the updater runs as the checkout's (non-root) owner"
id -nG "$OWNER" | tr ' ' '\n' | grep -qx docker || fail "$OWNER must be in the docker group (it runs the deploy)"
GROUP="$(id -gn "$OWNER")"

# ---- 1. cosign, pinned -------------------------------------------------------------
case "$(uname -m)" in
    x86_64) ARCH=amd64 ;;
    aarch64|arm64) ARCH=arm64 ;;
    *) fail "unsupported architecture $(uname -m)" ;;
esac
expected="${COSIGN_SHA256[$ARCH]}"
if [[ -x /usr/local/bin/cosign ]] && [[ "$(sha256sum /usr/local/bin/cosign | awk '{print $1}')" == "$expected" ]]; then
    log "cosign $COSIGN_VERSION already installed"
else
    download="$(mktemp)"
    trap 'rm -f -- "$download"' EXIT
    curl -fsSL -o "$download" \
        "https://github.com/sigstore/cosign/releases/download/${COSIGN_VERSION}/cosign-linux-${ARCH}"
    printf '%s  %s\n' "$expected" "$download" | sha256sum -c - >/dev/null || fail "cosign checksum mismatch"
    install -m 0755 -o root -g root "$download" /usr/local/bin/cosign
    log "installed cosign $COSIGN_VERSION"
fi

# ---- 2. Mailbox, and .env --------------------------------------------------------------
install -d -m 0750 -o "$OWNER" -g "$GROUP" "$MAILBOX"
current="$(sed -n -E 's/^[[:space:]]*(export[[:space:]]+)?NUBARCA_UPDATER_DIR=//p' "$ENV_FILE" | tail -n 1)"
current="${current%\"}"; current="${current#\"}"; current="${current%\'}"; current="${current#\'}"
if [[ -z "$current" ]]; then
    printf '\n# Mailbox shared by the api and the host updater (deploy/updater/install-updater.sh).\nNUBARCA_UPDATER_DIR=%s\n' \
        "$MAILBOX" >> "$ENV_FILE"
    log "recorded NUBARCA_UPDATER_DIR in the environment file"
elif [[ "$current" != "$MAILBOX" ]]; then
    fail "the environment file already names another mailbox ($current)"
fi

# ---- 3. Service ------------------------------------------------------------------------
cat > "/etc/systemd/system/$UNIT.tmp" <<EOF
[Unit]
Description=NubArca host updater (installs signed releases on an administrator's request)
Wants=docker.service
After=docker.service network-online.target

[Service]
Type=simple
User=$OWNER
Group=$GROUP
SupplementaryGroups=docker
WorkingDirectory=$CHECKOUT
ExecStart=/usr/bin/python3 $CHECKOUT/deploy/updater/nubarca-updater.py --mailbox $MAILBOX --checkout $CHECKOUT --env-file $ENV_FILE --auto-check-hours $AUTO_CHECK_HOURS
# It exits after every install so that the new release's updater takes over.
Restart=always
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF
mv -f "/etc/systemd/system/$UNIT.tmp" "/etc/systemd/system/$UNIT"
systemctl daemon-reload
systemctl enable "$UNIT" >/dev/null
systemctl restart "$UNIT"
log "installed and started $UNIT (runs as $OWNER)"

cat <<EOF

The updater is running. The api sees it once it mounts the mailbox: recreate it
with the complete release stack (deploy/FAST_DEPLOY.md), for example

  docker compose -f docker-compose.prod.yml -f docker-compose.prod.local.yml \\
    -f docker-compose.facedirect-api.yml -f docker-compose.release.local.yml \\
    --env-file .env up -d --no-build --no-deps api

(without docker-compose.facedirect-api.yml when NUBARCA_API_VARIANT=runtime).
Then Cloud functions -> Updates shows this installation's version and offers
the check and the install.
EOF

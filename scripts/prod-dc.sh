#!/usr/bin/env bash
# NubArca — base production docker compose helper.
#
# Wraps the base production Compose invocation so it is impossible to forget
# the local override file:
#
#   docker compose -f docker-compose.prod.yml -f docker-compose.prod.local.yml --env-file .env
#
# IMPORTANT: this is NOT the current release-deploy command. The production
# fastdeploy also stacks docker-compose.facedirect-api.yml and
# docker-compose.release.local.yml. Read deploy/FAST_DEPLOY.md before deploying.
#
# docker-compose.prod.local.yml is environment-specific (it lives on the
# production host, not in the repo). This helper always passes it; if it is
# absent (e.g. a dev checkout), docker compose will say so clearly.
#
# Usage:
#   ./scripts/prod-dc.sh                      # print the compose command and exit
#   ./scripts/prod-dc.sh --print              # same (explicit; also -h/--help)
#   ./scripts/prod-dc.sh ps                   # run: <compose> ps
#   ./scripts/prod-dc.sh up -d api frontend
#   ./scripts/prod-dc.sh run --rm api ai status
#
# Run from anywhere: the script resolves the repo root from its own location.

set -Eeuo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

dc=(docker compose
  -f docker-compose.prod.yml
  -f docker-compose.prod.local.yml
  --env-file .env)

# No args, or an explicit print/help flag: print the canonical command (so it
# can be copied or eval'd) and exit. Any other args are passed to docker compose.
if [[ $# -eq 0 || "${1:-}" == "--print" || "${1:-}" == "-h" || "${1:-}" == "--help" ]]; then
  printf '%s ' "${dc[@]}"
  printf '\n'
  exit 0
fi

exec "${dc[@]}" "$@"

#!/usr/bin/env bash
# The browser display, end to end, on a throwaway stack.
#
# Brings up a disposable PostgreSQL (docker), the API on :5177, an owner
# account, and the BUILT frontend under `vite preview` on :4173 — which
# proxies /api to the API exactly like the production front door does, so the
# display's cookie, origin and routes are the real ones. Then it runs
# frontend/scripts/tv-browser-e2e.mjs in a headless Chromium, and tears it all
# down whatever happened.
#
#   scripts/tv-browser-e2e.sh               # everything, locally
#   CHROME_BIN=/path/to/chrome scripts/tv-browser-e2e.sh
#
# Nothing here is an installation: every secret is a throwaway, every path is
# a temporary directory, and every address is loopback.

set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d -t nubarca-tv-e2e-XXXXXX)"
PG_PORT="${NUBARCA_E2E_PG_PORT:-55432}"
PG_CONTAINER="nubarca-tv-e2e-pg-$$"
API_PID=""
PREVIEW_PID=""

cleanup() {
  local status=$?
  [ -n "$PREVIEW_PID" ] && kill "$PREVIEW_PID" 2>/dev/null || true
  [ -n "$API_PID" ] && kill "$API_PID" 2>/dev/null || true
  docker rm -f "$PG_CONTAINER" >/dev/null 2>&1 || true
  if [ "$status" -ne 0 ]; then
    echo "--- API log (tail) ---" >&2
    tail -n 60 "$WORK/api.log" >&2 2>/dev/null || true
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

wait_for() {
  local what=$1 url=$2 tries=${3:-120}
  for _ in $(seq "$tries"); do
    if curl -fsS -o /dev/null "$url" 2>/dev/null; then return 0; fi
    sleep 1
  done
  echo "Timed out waiting for $what at $url" >&2
  return 1
}

echo "▶ PostgreSQL"
docker run -d --rm --name "$PG_CONTAINER" \
  -e POSTGRES_DB=nubarca -e POSTGRES_USER=nubarca -e POSTGRES_PASSWORD=nubarca \
  -p "127.0.0.1:${PG_PORT}:5432" pgvector/pgvector:pg17 >/dev/null
for _ in $(seq 60); do
  docker exec "$PG_CONTAINER" pg_isready -U nubarca -d nubarca >/dev/null 2>&1 && break
  sleep 1
done

export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:5177"
export ConnectionStrings__Postgres="Host=127.0.0.1;Port=${PG_PORT};Database=nubarca;Username=nubarca;Password=nubarca"
export Party__InvitationTokenSecret="e2e-only-invitation-secret"
export Party__CollaboratorOtpSecret="e2e-only-party-crew-otp-secret"
export Storage__RootPath="$WORK/storage"
export TvUpdates__RootPath="$WORK/tv-updates"
export Database__MigrateOnStartup=true
mkdir -p "$Storage__RootPath" "$TvUpdates__RootPath"

echo "▶ API"
dotnet build "$ROOT/src/NubArca.Api/NubArca.Api.csproj" -c Release -o "$WORK/api" -v quiet -nologo >/dev/null
( cd "$WORK/api" && exec dotnet NubArca.Api.dll ) >"$WORK/api.log" 2>&1 &
API_PID=$!
wait_for "the API" "http://127.0.0.1:5177/health"

export NUBARCA_E2E_EMAIL="owner@nubarca.test"
export NUBARCA_E2E_PASSWORD="e2e-owner-password"
( cd "$WORK/api" \
  && dotnet NubArca.Api.dll users ensure --email "$NUBARCA_E2E_EMAIL" --display-name "E2E Owner" \
       --password "$NUBARCA_E2E_PASSWORD" \
  && dotnet NubArca.Api.dll users grant-admin --email "$NUBARCA_E2E_EMAIL" ) >/dev/null

echo "▶ Frontend"
cd "$ROOT/frontend"
npm run build --silent >/dev/null 2>&1
npx vite preview --port 4173 --strictPort >"$WORK/preview.log" 2>&1 &
PREVIEW_PID=$!
wait_for "the frontend" "http://localhost:4173/tv"

echo "▶ The display"
APP="http://localhost:4173" node "$ROOT/frontend/scripts/tv-browser-e2e.mjs"

#!/usr/bin/env bash
# NubArca — unauthenticated smoke check.
#
# Hits a few well-known endpoints and reports pass/fail per check. This
# script DELIBERATELY only exercises UNAUTHENTICATED paths so it does not
# need a plaintext password and cannot accidentally mutate production data.
# Login + upload + share-link checks are documented as manual steps in
# SMOKE_CHECKLIST.md so the operator runs them in a private browser
# window — never via a shell script that might dump a password to logs.
#
# Usage:
#   BASE_URL=https://nubarca.example.com ./deploy/smoke-check.sh
#   BASE_URL=http://127.0.0.1:8080         ./deploy/smoke-check.sh   # local
#
# Exit code 0  → all checks passed.
# Exit code 1  → at least one check failed (details on stderr).
# Exit code 64 → usage error (missing BASE_URL).

set -Eeuo pipefail

BASE_URL="${BASE_URL:-}"
if [ -z "$BASE_URL" ]; then
    cat >&2 <<EOF
Usage: BASE_URL=https://your-domain.example ./deploy/smoke-check.sh

Set BASE_URL to the public origin in front of NubArca (the reverse-proxy
URL on a real deploy, or http://127.0.0.1:8080 against the api container
directly for local sanity).
EOF
    exit 64
fi

# Strip trailing slashes so we can paste paths without doubling them.
BASE_URL="${BASE_URL%/}"

pass=0
fail=0
checks=()

ok()    { checks+=("PASS  $*"); pass=$((pass+1)); }
ko()    { checks+=("FAIL  $*"); fail=$((fail+1)); }
note()  { checks+=("info  $*"); }

# All curl calls run with:
#   -s        no progress bar
#   -S        still show errors
#   -o /dev/null OR -o tmpfile  ignore / capture body
#   -w '%{http_code}'  print status code so we can compare
#   -I (head) where we only care about headers
# Connect timeout 10s, total timeout 20s — generous but bounded.

curl_status() {
    # Usage: curl_status <method> <path> [extra-args...]
    local method="$1"; shift
    local path="$1"; shift
    curl -sS \
        --connect-timeout 10 --max-time 20 \
        -o /dev/null \
        -w '%{http_code}' \
        -X "$method" \
        "$@" \
        "$BASE_URL$path" \
        || echo "000"
}

# ---- 1. /health --------------------------------------------------------

body_tmp="$(mktemp)"
trap 'rm -f "$body_tmp"' EXIT
status="$(curl -sS --connect-timeout 10 --max-time 20 \
    -o "$body_tmp" -w '%{http_code}' "$BASE_URL/health" || echo "000")"
body="$(cat "$body_tmp")"

if [ "$status" = "200" ] && printf '%s' "$body" | grep -q '"status"'; then
    ok "GET /health → 200 + {\"status\":...}"
else
    ko "GET /health → status=$status body_size=$(printf '%s' "$body" | wc -c)"
fi

# ---- 2. SPA index ------------------------------------------------------

body_tmp2="$(mktemp)"
# shellcheck disable=SC2064
trap "rm -f \"$body_tmp\" \"$body_tmp2\"" EXIT
status="$(curl -sS --connect-timeout 10 --max-time 20 \
    -o "$body_tmp2" -w '%{http_code}' "$BASE_URL/" || echo "000")"
body="$(cat "$body_tmp2")"

if [ "$status" = "200" ] && printf '%s' "$body" | grep -qi '<!doctype html\|<html'; then
    ok "GET /   → 200 + HTML (SPA bundle)"
else
    ko "GET /   → status=$status, body did not look like the SPA shell"
fi

# ---- 3. /api/auth/me must be 401 without a cookie ----------------------

status="$(curl_status GET /api/auth/me)"
if [ "$status" = "401" ]; then
    ok "GET /api/auth/me  → 401 (auth pipeline + proxy path /api/* wired)"
else
    ko "GET /api/auth/me  → status=$status (expected 401)"
fi

# ---- 4. Bogus share token must 404 (NOT 200, NOT a stack trace) --------

bogus_token="smoke-check-not-a-real-token-$(date +%s)"
status="$(curl_status GET "/s/$bogus_token")"
if [ "$status" = "404" ]; then
    ok "GET /s/<bogus-token> → 404 (public share route reachable, no info leak)"
elif [ "$status" = "429" ]; then
    note "GET /s/<bogus-token> → 429 (rate-limited; share-public limiter is active)"
    ok "  (treating 429 as pass — the public share route IS reachable)"
else
    ko "GET /s/<bogus-token> → status=$status (expected 404)"
fi

# ---- 5. HSTS header (only meaningful behind HTTPS) ---------------------

if printf '%s' "$BASE_URL" | grep -q '^https://'; then
    hsts="$(curl -sS --connect-timeout 10 --max-time 20 -I "$BASE_URL/" 2>/dev/null \
        | tr -d '\r' | awk -F': ' 'tolower($1) == "strict-transport-security" {print $2; exit}')"
    if [ -n "$hsts" ]; then
        ok "Strict-Transport-Security present (max-age=${hsts%%[!0-9]*}...)"
    else
        note "Strict-Transport-Security missing — recommend adding it in the reverse-proxy config."
    fi
else
    note "BASE_URL is plain HTTP; skipping HSTS check (only meaningful behind HTTPS)."
fi

# ---- summary -----------------------------------------------------------

printf '\nNubArca smoke check  (BASE_URL=%s)\n' "$BASE_URL"
printf '%s\n' "${checks[@]}"
printf '\n  passed: %d   failed: %d\n\n' "$pass" "$fail"

if [ "$fail" -gt 0 ]; then
    cat <<'EOF' >&2
At least one automated smoke check failed. Look at SMOKE_CHECKLIST.md's
"Failure -> action mapping" table for the most common root causes, and
check `docker compose -f docker-compose.prod.yml --env-file .env logs api
--tail=50` for the api-side detail.
EOF
    exit 1
fi

cat <<'EOF'
Automated checks passed. Next: run the MANUAL checks in
deploy/SMOKE_CHECKLIST.md (login, upload, share link) from a private
browser window. Then take a backup and do the restore drill on a separate
host BEFORE storing real data.
EOF

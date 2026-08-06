#!/usr/bin/env bash
# Create the deterministic test state.
#
# Users come from the canonical administrative command inside the API container;
# everything else is created through the product's public API by src/seed.ts.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

require_tool ffmpeg "media fixtures are generated rather than committed"

say "seeding users"
ensure_user() {
  local email="$1" display="$2" password="$3"
  dc exec -T api dotnet NubArca.Api.dll users ensure \
    --email "$email" --display-name "$display" --password "$password" \
    --update-password 2>&1 | sed 's/^/   /'
}
# Credentials are throwaway values for a throwaway database; see src/env.ts.
ensure_user "owner@nubarca.test" "E2E Owner"       "e2e-owner-password"
ensure_user "other@nubarca.test" "E2E Other Owner" "e2e-other-password"

# The deterministic backend needs its DEV/TEST profiles to exist before anything
# is uploaded, or the post-ingestion pipeline has no profile to write artifacts
# against and semantic search answers "no-default-profile".
say "seeding deterministic AI profiles"
dc exec -T api dotnet NubArca.Api.dll ai seed 2>&1 | sed 's/^/   /'

say "seeding media, albums and semantic data"
cd "$E2E_ROOT"
npx tsx src/seed.ts

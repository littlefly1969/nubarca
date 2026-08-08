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
set_role() {
  local email="$1" role="$2"
  dc exec -T api dotnet NubArca.Api.dll users set-role \
    --email "$email" --role "$role" 2>&1 | sed 's/^/   /'
}
# Credentials are throwaway values for a throwaway database; see src/env.ts.
# OWNER and OTHER stay Members — the role every pre-role account migrated to —
# so the existing specs keep asserting the navigation they always did.
ensure_user "owner@nubarca.test" "E2E Owner"       "e2e-owner-password"
ensure_user "other@nubarca.test" "E2E Other Owner" "e2e-other-password"

# Identity & Access fixtures. One account per authority, so a spec never has to
# mutate a shared one to observe a different one.
ensure_user "admin@nubarca.test"      "E2E Admin"      "e2e-admin-password"
ensure_user "restricted@nubarca.test" "E2E Restricted" "e2e-restricted-password"
ensure_user "grantable@nubarca.test"  "E2E Grantable"  "e2e-grantable-password"
ensure_user "labplates@nubarca.test"  "E2E Lab Plates" "e2e-labplates-password"
ensure_user "recovery@nubarca.test"   "E2E Recovery"   "e2e-recovery-password"

set_role "admin@nubarca.test"      Administrator
set_role "restricted@nubarca.test" Restricted
set_role "grantable@nubarca.test"  Restricted
set_role "labplates@nubarca.test"  Restricted

# The deterministic backend needs its DEV/TEST profiles to exist before anything
# is uploaded, or the post-ingestion pipeline has no profile to write artifacts
# against and semantic search answers "no-default-profile".
say "seeding deterministic AI profiles"
dc exec -T api dotnet NubArca.Api.dll ai seed 2>&1 | sed 's/^/   /'

say "seeding media, albums and semantic data"
cd "$E2E_ROOT"
npx tsx src/seed.ts

#!/usr/bin/env bash
# One command: prerequisites -> stack -> seed -> browser matrix -> verify -> teardown.
#
# Which projects run is decided by the caller: `npm run e2e` selects the three
# Chromium projects that form the release gate, `npm run e2e:extended` runs the
# whole nine-project matrix as an optional diagnostic.
#
# Teardown runs from a trap, so a failing test still leaves the host clean while
# preserving the report and traces needed to diagnose the failure.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

say "1. prerequisites"
require_tool docker
require_tool curl
require_tool node
require_tool npx
require_tool ffmpeg "media fixtures are generated rather than committed"
docker info >/dev/null 2>&1 || die "the Docker daemon is not reachable"
[ -d "$E2E_ROOT/node_modules" ] || die "run 'npm install' in tests/e2e first"
[ -d "$REPO_ROOT/frontend/node_modules" ] || die "run 'npm install' in frontend first"
info "all prerequisites present"

EXIT_CODE=0

cleanup() {
  local code=$?
  set +e
  if [ "$code" -ne 0 ]; then
    say "preserving diagnostics (exit $code)"
    mkdir -p "$ARTIFACT_DIR"
    dc logs --no-color --tail 400 > "$ARTIFACT_DIR/stack.log" 2>&1 || true
    info "stack logs:      tests/e2e/.artifacts/stack.log"
    info "playwright HTML: tests/e2e/playwright-report/index.html"
    info "traces:          tests/e2e/test-results/"
  fi
  "$E2E_ROOT/scripts/stop.sh" || true
  exit "$code"
}
trap cleanup EXIT INT TERM

say "2. ephemeral stack"
"$E2E_ROOT/scripts/start.sh"

say "3. seed"
"$E2E_ROOT/scripts/seed.sh"

say "4. browser matrix"
# The exit code is captured, never piped away: a pipeline reports its LAST
# command's status, which is how a failed run can read as green.
"$E2E_ROOT/scripts/test.sh" "$@" || EXIT_CODE=$?

# Runs on failure too. A failing run must still be checked for internal
# consistency, because "52 failed" and "the report disagrees with the exit code"
# are different problems and only one of them is a test failure.
say "5. authoritative result"
"$E2E_ROOT/scripts/verify-report.sh" "$EXIT_CODE" "${E2E_EXPECTED_TESTS:-}"

say "6. matrix passed"
exit 0

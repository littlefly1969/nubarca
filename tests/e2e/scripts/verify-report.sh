#!/usr/bin/env bash
# Make the result authoritative.
#
# A line reporter scrolling past is not a result. Neither is a pipeline's exit
# status: `npx playwright test | tail` reports tail's success and has already
# produced one false green here. This script is the only thing allowed to say
# the matrix passed, and it says so only when three independent sources agree:
#
#   1. the exit code Playwright actually returned,
#   2. the machine-readable JSON report's own pass/fail totals,
#   3. the number of tests the gate is contractually required to run.
#
# (3) is what catches the failure a green run cannot: a project silently not
# matching, a spec file renamed out of testDir, a testMatch that quietly stopped
# selecting. Zero failures out of zero tests is not a pass.
set -euo pipefail

. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

PLAYWRIGHT_EXIT="${1:?usage: verify-report.sh <playwright-exit-code> [expected-test-count]}"
EXPECTED="${2:-${E2E_EXPECTED_TESTS:-}}"
REPORT="$ARTIFACT_DIR/results.json"

say "verifying the run is internally consistent"

[ -f "$REPORT" ] || die "no JSON report at $REPORT — the run produced no machine-readable result"

# Totals come from the report's own per-test statuses rather than from a summary
# line, so a truncated or half-written report cannot read as a pass.
read -r total passed failed flaky skipped < <(
  node --input-type=module -e '
    import { readFileSync } from "node:fs";
    const report = JSON.parse(readFileSync(process.argv[1], "utf8"));
    const counts = { passed: 0, failed: 0, flaky: 0, skipped: 0 };
    let total = 0;
    const walk = (suite) => {
      for (const child of suite.suites ?? []) walk(child);
      for (const spec of suite.specs ?? []) {
        for (const test of spec.tests ?? []) {
          total += 1;
          const status = test.status ?? "unknown";
          counts[status] = (counts[status] ?? 0) + 1;
        }
      }
    };
    for (const suite of report.suites ?? []) walk(suite);
    // "expected" is Playwright JSON for a test that ended as intended.
    const passedCount = (counts.expected ?? 0) + (counts.passed ?? 0);
    const failedCount = (counts.unexpected ?? 0) + (counts.failed ?? 0);
    // Trailing newline matters: `read` returns non-zero at EOF without one,
    // and under `set -e` that aborts this script after a perfectly good run.
    process.stdout.write([
      total, passedCount, failedCount, counts.flaky ?? 0, counts.skipped ?? 0,
    ].join(" ") + "\n");
  ' "$REPORT"
)

info "playwright exit code: $PLAYWRIGHT_EXIT"
info "json totals:          total=$total passed=$passed failed=$failed flaky=$flaky skipped=$skipped"
info "expected test count:  ${EXPECTED:-<unset>}"

ok=1

if [ -n "$EXPECTED" ] && [ "$total" -ne "$EXPECTED" ]; then
  printf '   MISMATCH: the gate ran %s tests but must run %s\n' "$total" "$EXPECTED" >&2
  ok=0
fi

if [ "$failed" -ne 0 ] || [ "$flaky" -ne 0 ] || [ "$skipped" -ne 0 ]; then
  printf '   MISMATCH: the report is not all-passed (failed=%s flaky=%s skipped=%s)\n' \
    "$failed" "$flaky" "$skipped" >&2
  ok=0
fi

if [ "$passed" -ne "$total" ]; then
  printf '   MISMATCH: %s of %s tests passed\n' "$passed" "$total" >&2
  ok=0
fi

# The two sources must not disagree in either direction. A zero exit with
# failures in the report, or a non-zero exit with a clean report, means the
# result cannot be trusted at all — that is worse than a plain failure.
if [ "$PLAYWRIGHT_EXIT" -eq 0 ] && [ "$failed" -ne 0 ]; then
  printf '   MISMATCH: playwright exited 0 while the report records %s failures\n' "$failed" >&2
  ok=0
fi

if [ "$PLAYWRIGHT_EXIT" -ne 0 ] && [ "$failed" -eq 0 ] && [ "$ok" -eq 1 ]; then
  printf '   MISMATCH: playwright exited %s while the report records no failure\n' "$PLAYWRIGHT_EXIT" >&2
  ok=0
fi

[ "$ok" -eq 1 ] || die "the run is not authoritative — see the mismatches above"
[ "$PLAYWRIGHT_EXIT" -eq 0 ] || die "playwright exited $PLAYWRIGHT_EXIT"

info "AUTHORITATIVE: $passed/$total passed, exit code 0, all three sources agree"

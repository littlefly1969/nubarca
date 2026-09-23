#!/usr/bin/env python3
"""A SKIPPED RACE IS NOT A PASSED ONE.

The Album Share concurrency tests run against real PostgreSQL through
Testcontainers, and the fixture answers `Skip.IfNot(Available)` when Docker is
missing so that a developer's laptop does not fail the whole suite. On CI that
kindness becomes a hole: a runner without a working container leaves every race
skipped, `dotnet test` exits 0, and the job goes green having verified nothing —
while the invariants those tests exist to gate are the ones this feature's
correctness rests on.

So the gate asserts the outcome rather than the exit code: every named race must
be present in the results, and every one of them must have PASSED. A rename that
drops one silently is caught here too, which is why the list is written out
instead of being counted.
"""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ET
from pathlib import Path

# The races, by name. Written out rather than counted so that deleting or
# renaming one is a failure of this gate and not a quiet reduction of it.
REQUIRED = {
    "Two_creates_at_the_same_instant_leave_one_live_link",
    "A_rotation_racing_a_removal_never_carries_the_removed_address_forward",
    "Two_resends_produce_one_challenge_and_one_usable_code",
    "Fifty_one_addresses_arriving_together_do_not_exceed_the_ceiling",
    "A_reactivation_racing_a_new_address_for_the_last_slot_respects_the_ceiling",
}

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def outcomes(results_dir: Path) -> dict[str, str]:
    """Every test name in the trx files, mapped to its outcome."""
    found: dict[str, str] = {}
    for trx in results_dir.rglob("*.trx"):
        for result in ET.parse(trx).getroot().iter(f"{{{NS['t']}}}UnitTestResult"):
            name = result.get("testName") or ""
            # The trx carries the method name, sometimes with a class prefix.
            short = name.rsplit(".", 1)[-1].split("(", 1)[0]
            found[short] = result.get("outcome") or "Unknown"
    return found


def main() -> int:
    if len(sys.argv) != 2:
        print("usage: assert-concurrency-gate.py <results-directory>", file=sys.stderr)
        return 2

    results_dir = Path(sys.argv[1])
    if not results_dir.exists():
        print(f"FAIL: no results at {results_dir}", file=sys.stderr)
        return 1

    found = outcomes(results_dir)
    problems: list[str] = []

    for name in sorted(REQUIRED):
        outcome = found.get(name)
        if outcome is None:
            problems.append(f"  {name}: NOT FOUND in the results")
        elif outcome != "Passed":
            # `NotExecuted` is what a Skip produces, and it is the case this
            # script exists for: Docker was unavailable and the race never ran.
            hint = " (skipped — was Docker available?)" if outcome == "NotExecuted" else ""
            problems.append(f"  {name}: {outcome}{hint}")

    if problems:
        print(
            "FAIL: the Album Share concurrency gate did not run clean.\n"
            + "\n".join(problems),
            file=sys.stderr,
        )
        return 1

    print(f"Album Share concurrency gate: {len(REQUIRED)} races ran and passed.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

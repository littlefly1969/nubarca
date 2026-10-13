#!/usr/bin/env python3
"""Split the fast backend tests into balanced shards, by test class.

CI runs the fast lane as N parallel jobs. Each job lists the tests of the
build it just made, and this script turns that list into a VSTest filter for
its own shard. The split is by class (a class's tests share fixtures, so
splitting one costs more than it saves) and balanced on the recorded class
durations in tests/backend-test-timings.json: the longest classes are placed
first, each on the least loaded shard. A class with no recorded duration
(added since the file was last refreshed) counts as the median one, and no
class counts for less than SECONDS_PER_TEST per test. Every job
computes the same partition from the same list, so the shards together cover
every class exactly once; the script checks that before printing a filter.

  filter   --list LIST --shard I --shards N    print shard I's filter (1-based)
  plan     --list LIST --shards N              print every shard's estimate
  timings  TRX...                              rewrite the timings file from
                                               .trx results: preferably all
                                               eight CI slices of one run

LIST is the output of `dotnet test --list-tests`.
"""

import argparse
import json
import statistics
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
TIMINGS = REPO / "tests" / "backend-test-timings.json"
LISTED = "    "


def class_of(test_name):
    """`Ns.Class.Method(args...)` -> `Ns.Class`."""
    return test_name.split("(", 1)[0].rsplit(".", 1)[0]


def listed_tests(path):
    lines = Path(path).read_text(encoding="utf-8").splitlines()
    tests = [line.strip() for line in lines if line.startswith(LISTED) and line.strip()]
    if not tests:
        sys.exit(f"no tests found in {path}: is it `dotnet test --list-tests` output?")
    return tests


def class_counts(tests):
    counts = {}
    for test in tests:
        cls = class_of(test)
        counts[cls] = counts.get(cls, 0) + 1
    return counts


def load_timings():
    if not TIMINGS.exists():
        return {}
    return json.loads(TIMINGS.read_text(encoding="utf-8"))["classes"]


# Every test costs at least this much (a host to start, a database to clone),
# so a class recorded as 0.0 seconds is never treated as free and classes of
# many fast tests do not all pile onto one slice.
SECONDS_PER_TEST = 0.1


def partition(counts, shards, timings):
    """counts: {class: number of listed tests}."""
    classes = sorted(counts)
    known = [timings[c] for c in classes if c in timings]
    fallback = statistics.median(known) if known else 1.0
    cost = {
        c: max(timings.get(c, fallback), SECONDS_PER_TEST * counts[c])
        for c in classes
    }

    loads = [0.0] * shards
    members = [[] for _ in range(shards)]
    for cls in sorted(classes, key=lambda c: (-cost[c], c)):
        target = min(range(shards), key=lambda i: (loads[i], i))
        loads[target] += cost[cls]
        members[target].append(cls)
    return members, loads


def pattern(cls):
    # The trailing dot keeps `FooTests.` from also matching `FooTestsBar`.
    return f"FullyQualifiedName~{cls}."


def check_cover(tests, members):
    """Every listed test matches the patterns of exactly one shard."""
    names = [t.split("(", 1)[0] for t in tests]
    for name in names:
        hits = sum(
            1 for shard in members if any(f"{cls}." in name for cls in shard)
        )
        if hits != 1:
            sys.exit(f"test {name} matches {hits} shards; the split would "
                     "skip or repeat it")


def cmd_filter(args):
    tests = listed_tests(args.list)
    members, _ = partition(class_counts(tests), args.shards, load_timings())
    check_cover(tests, members)
    if not 1 <= args.shard <= args.shards:
        sys.exit(f"--shard must be between 1 and {args.shards}")
    print("|".join(pattern(c) for c in members[args.shard - 1]))


def cmd_plan(args):
    tests = listed_tests(args.list)
    counts = class_counts(tests)
    classes = sorted(counts)
    timings = load_timings()
    members, loads = partition(counts, args.shards, timings)
    check_cover(tests, members)
    unknown = [c for c in classes if c not in timings]
    for i, (shard, load) in enumerate(zip(members, loads), start=1):
        print(f"shard {i}: {len(shard):3d} classes, {load / 60:6.1f} min of test time")
    print(f"{len(classes)} classes, {len(unknown)} without a recorded duration")


def cmd_timings(args):
    ns = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}
    totals = {}
    for trx in args.trx:
        root = ET.parse(trx).getroot()
        for result in root.iterfind(".//t:UnitTestResult", ns):
            duration = result.get("duration")
            if duration is None:
                continue
            h, m, s = duration.split(":")
            seconds = int(h) * 3600 + int(m) * 60 + float(s)
            cls = class_of(result.get("testName"))
            totals[cls] = totals.get(cls, 0.0) + seconds
    if not totals:
        sys.exit("no test results found in the given .trx files")
    document = {
        "about": "Seconds of test time per class in the fast backend lane, "
                 "used by scripts/backend-test-shards.py to balance the CI "
                 "shards. Refresh with: scripts/backend-test-shards.py "
                 "timings <results.trx>",
        "classes": {c: round(totals[c], 1) for c in sorted(totals)},
    }
    TIMINGS.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"wrote {len(totals)} classes to {TIMINGS.relative_to(REPO)}")


def main():
    parser = argparse.ArgumentParser(description=__doc__,
                                     formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = parser.add_subparsers(dest="command", required=True)

    f = sub.add_parser("filter")
    f.add_argument("--list", required=True)
    f.add_argument("--shard", type=int, required=True)
    f.add_argument("--shards", type=int, required=True)
    f.set_defaults(run=cmd_filter)

    p = sub.add_parser("plan")
    p.add_argument("--list", required=True)
    p.add_argument("--shards", type=int, required=True)
    p.set_defaults(run=cmd_plan)

    t = sub.add_parser("timings")
    t.add_argument("trx", nargs="+")
    t.set_defaults(run=cmd_timings)

    args = parser.parse_args()
    args.run(args)


if __name__ == "__main__":
    main()

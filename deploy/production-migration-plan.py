#!/usr/bin/env python3
"""Validate and print the migrations eligible for automated production apply."""

from __future__ import annotations

import json
import re
import subprocess
import sys


MIGRATION_ROOT = "src/NubArca.Api/Data/Migrations/"
SNAPSHOT = f"{MIGRATION_ROOT}AppDbContextModelSnapshot.cs"
BASE_PATTERN = re.compile(r"^(\d{14}_[A-Za-z0-9_]+)\.cs$")
DESIGNER_PATTERN = re.compile(r"^(\d{14}_[A-Za-z0-9_]+)\.Designer\.cs$")


def fail(message: str) -> None:
    print(f"production migration plan: {message}", file=sys.stderr)
    raise SystemExit(2)


def git(*args: str) -> str:
    completed = subprocess.run(
        ["git", *args],
        check=False,
        text=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if completed.returncode != 0:
        fail(completed.stderr.strip() or f"git {' '.join(args)} failed")
    return completed.stdout


def main() -> None:
    if len(sys.argv) != 3:
        fail("usage: production-migration-plan.py <from-sha> <to-sha>")

    from_sha, to_sha = sys.argv[1:]
    ancestry = subprocess.run(
        ["git", "merge-base", "--is-ancestor", from_sha, to_sha],
        check=False,
        stdout=subprocess.DEVNULL,
        stderr=subprocess.PIPE,
        text=True,
    )
    if ancestry.returncode != 0:
        fail("running API source is not an ancestor of the candidate release")
    diff = git(
        "diff",
        "--name-status",
        "--no-renames",
        f"{from_sha}..{to_sha}",
        "--",
        MIGRATION_ROOT,
    )
    entries: list[tuple[str, str]] = []
    for line in diff.splitlines():
        status, separator, path = line.partition("\t")
        if not separator:
            fail(f"cannot parse migration diff line: {line!r}")
        entries.append((status, path))

    if not entries:
        return

    bases: set[str] = set()
    designers: set[str] = set()
    snapshot_seen = False
    for status, path in entries:
        if path == SNAPSHOT:
            if status != "M":
                fail(f"snapshot must be modified, got {status}: {path}")
            snapshot_seen = True
            continue

        name = path.removeprefix(MIGRATION_ROOT)
        base_match = BASE_PATTERN.fullmatch(name)
        designer_match = DESIGNER_PATTERN.fullmatch(name)
        if status != "A" or (base_match is None and designer_match is None):
            fail(f"only additive migration files are automatic, got {status}: {path}")
        if designer_match:
            designers.add(designer_match.group(1))
        elif base_match:
            bases.add(base_match.group(1))

    if not snapshot_seen:
        fail("migration set does not update AppDbContextModelSnapshot.cs")
    if not bases:
        fail("migration diff contains no new migration implementation")
    if bases != designers:
        missing_designers = sorted(bases - designers)
        missing_bases = sorted(designers - bases)
        fail(
            "migration implementation/designer mismatch: "
            f"missing designers={missing_designers}, missing implementations={missing_bases}"
        )

    try:
        policy = json.loads(git("show", f"{to_sha}:deploy/migration-policy.json"))
    except json.JSONDecodeError as error:
        fail(f"candidate migration policy is invalid JSON: {error}")

    if policy.get("schemaVersion") != 1 or not isinstance(policy.get("migrations"), dict):
        fail("candidate migration policy has an unsupported schema")

    for migration_id in sorted(bases):
        rule = policy["migrations"].get(migration_id)
        if not isinstance(rule, dict):
            fail(f"{migration_id} has no production automation policy")
        if rule.get("automated") is not True:
            fail(f"{migration_id} is not approved for automated application")
        if rule.get("previousApplicationCompatible") is not True:
            fail(f"{migration_id} does not permit application-image rollback")
        reason = rule.get("reason")
        if not isinstance(reason, str) or not reason.strip():
            fail(f"{migration_id} policy must explain its compatibility argument")

    print("\n".join(sorted(bases)))


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Every merge into main is a release, so every pull request into main raises
the release version.

The rule (CLAUDE.md, "Release version"): a normal change raises the patch
(0.4.0 -> 0.4.1); the minor goes up (0.4.x -> 0.5.0) only when the owner names
a more important release. This check enforces only "strictly greater than
main", which both satisfy; scripts/check-nubarca-identity.sh already keeps the
backend, the frontend and the changelog on one version.

  check-release-version.py BASE_CSPROJ   compare the working tree with main's
                                         NubArca.Api.csproj, checked out there
  check-release-version.py --self-test   prove the comparison
"""

import re
import sys
from pathlib import Path

API_PROJECT = Path("src/NubArca.Api/NubArca.Api.csproj")


def version_of(csproj_text: str) -> tuple[int, int, int]:
    match = re.search(r"<Version>\s*(\d+)\.(\d+)\.(\d+)\s*</Version>", csproj_text)
    if match is None:
        raise ValueError("no plain <Version>MAJOR.MINOR.PATCH</Version>")
    return tuple(int(part) for part in match.groups())


def text(version: tuple[int, int, int]) -> str:
    return ".".join(str(part) for part in version)


def self_test() -> int:
    def project(version: str) -> str:
        return f"<Project><PropertyGroup><Version>{version}</Version></PropertyGroup></Project>"

    cases = [
        ("0.4.1", "0.4.0", True),
        ("0.5.0", "0.4.9", True),
        ("0.4.10", "0.4.9", True),
        ("1.0.0", "0.9.9", True),
        ("0.4.0", "0.4.0", False),
        ("0.3.9", "0.4.0", False),
        ("0.4.9", "0.4.10", False),
    ]
    failures = 0
    for head, base, expected in cases:
        got = version_of(project(head)) > version_of(project(base))
        if got != expected:
            failures += 1
            print(f"FAIL: {head} after {base}: expected {expected}, got {got}")
    for malformed in ("0.4", "v0.4.1", "0.4.1-beta"):
        try:
            version_of(project(malformed))
        except ValueError:
            continue
        failures += 1
        print(f"FAIL: accepted malformed version {malformed}")
    total = len(cases) + 3
    if failures:
        print(f"release version self-test: {failures} of {total} cases wrong")
        return 1
    print(f"release version self-test: {total} cases correct")
    return 0


def main(argv: list[str]) -> int:
    if argv == ["--self-test"]:
        return self_test()
    if len(argv) != 1:
        print(__doc__, file=sys.stderr)
        return 2
    base = version_of(Path(argv[0]).read_text(encoding="utf-8"))
    head = version_of(API_PROJECT.read_text(encoding="utf-8"))
    if head <= base:
        print(
            f"Release version {text(head)} is not above main's {text(base)}. "
            f"Every merge into main is a release: raise the patch "
            f"({text((base[0], base[1], base[2] + 1))}) in {API_PROJECT}, "
            "frontend/package.json and CHANGELOG.md, or the minor when the "
            "owner names a more important release.",
            file=sys.stderr,
        )
        return 1
    print(f"Release version {text(head)} follows main's {text(base)}.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))

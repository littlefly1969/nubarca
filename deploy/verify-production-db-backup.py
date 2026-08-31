#!/usr/bin/env python3
"""Fail unless a compressed production pg_dump is complete and migration-aware."""

from __future__ import annotations

import gzip
import sys


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit("usage: verify-production-db-backup.py <dump.sql.gz>")

    history = False
    complete = False
    try:
        with gzip.open(sys.argv[1], "rt", encoding="utf-8", errors="replace") as stream:
            for line in stream:
                stripped = line.rstrip("\r\n")
                if "__EFMigrationsHistory" in stripped:
                    history = True
                if stripped == "-- PostgreSQL database dump complete":
                    complete = True
    except (OSError, EOFError) as error:
        raise SystemExit(f"cannot read complete gzip stream: {error}") from error

    if not history:
        raise SystemExit("dump does not contain __EFMigrationsHistory")
    if not complete:
        raise SystemExit("dump has no clean completion marker")


if __name__ == "__main__":
    main()

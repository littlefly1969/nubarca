#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_dir/tests/NubArca.Api.Tests/NubArca.Api.Tests.csproj"

# Run deterministic/local coverage first. Keeping Docker, real FFmpeg and live
# sidecar tests in a second process prevents resource-heavy fixtures from
# starving the broad SQLite suite and makes the slow boundary visible.
fast_status=0
external_status=0

dotnet test "$project" --filter "Category!=External" "$@" || fast_status=$?
dotnet test "$project" --no-restore --no-build --filter "Category=External" "$@" || external_status=$?

if (( fast_status != 0 || external_status != 0 )); then
    exit 1
fi

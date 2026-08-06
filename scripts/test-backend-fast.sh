#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_dir/tests/NubArca.Api.Tests/NubArca.Api.Tests.csproj"

dotnet test "$project" --filter "Category!=External" "$@"

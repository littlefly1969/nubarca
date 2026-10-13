#!/usr/bin/env bash
set -euo pipefail

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_dir/tests/NubArca.Api.Tests/NubArca.Api.Tests.csproj"

# --shard I/N runs only the I-th of N balanced slices of the suite (by test
# class, see scripts/backend-test-shards.py). CI runs every slice in parallel;
# without --shard the whole fast suite runs, as before.
shard=""
built=0
build_args=()
args=()
while (($#)); do
    case "$1" in
        --shard)
            shard="$2"
            shift 2
            ;;
        --configuration|-c)
            build_args+=("$1" "$2")
            args+=("$1" "$2")
            shift 2
            ;;
        --no-build)
            built=1
            args+=("$1")
            shift
            ;;
        *)
            args+=("$1")
            shift
            ;;
    esac
done

filter="Category!=External"
if [[ -n "$shard" ]]; then
    if [[ ! "$shard" =~ ^([0-9]+)/([0-9]+)$ ]]; then
        echo "--shard expects I/N, for example 2/8" >&2
        exit 2
    fi
    list="$(mktemp)"
    trap 'rm -f "$list"' EXIT
    # Listing reads the built assembly, in the configuration the run will use.
    if (( ! built )); then
        dotnet build "$project" --nologo -v quiet "${build_args[@]}" >/dev/null
    fi
    dotnet test "$project" --no-build --list-tests "${build_args[@]}" >"$list"
    classes="$("$repo_dir/scripts/backend-test-shards.py" filter \
        --list "$list" --shard "${BASH_REMATCH[1]}" --shards "${BASH_REMATCH[2]}")"
    filter="($filter)&($classes)"
fi

dotnet test "$project" --filter "$filter" "${args[@]}"

#!/usr/bin/env bash
# Propagate the canonical /video delivery contract to its three consumers.
#
# shared/video-delivery/videoDelivery.ts is the single source of truth for how
# a probed /video response is classified and retried. frontend, mobile and tv
# are three independent npm projects with three toolchains and no workspace
# root, so the contract is VENDORED into each of them as a byte-identical copy
# rather than resolved through a package (see the header of the canonical file
# for why). This script is how a copy is made, and `--check` is how CI proves
# none of them has drifted — each project's own videoDelivery test asserts the
# same byte identity, so a divergence fails the normal test lanes too.
#
#   scripts/sync-video-delivery.sh            # copy canonical -> consumers
#   scripts/sync-video-delivery.sh --check    # fail if any copy differs
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source_file="$root/shared/video-delivery/videoDelivery.ts"

targets=(
  "$root/frontend/src/video/videoDelivery.ts"
  "$root/mobile/src/media/videoDelivery.ts"
  "$root/tv/src/video/videoDelivery.ts"
)

if [[ ! -f "$source_file" ]]; then
  echo "missing canonical contract: $source_file" >&2
  exit 1
fi

mode="sync"
if [[ "${1:-}" == "--check" ]]; then
  mode="check"
elif [[ $# -gt 0 ]]; then
  echo "usage: $(basename "$0") [--check]" >&2
  exit 2
fi

status=0
for target in "${targets[@]}"; do
  rel="${target#"$root"/}"
  if [[ "$mode" == "check" ]]; then
    if ! cmp -s "$source_file" "$target"; then
      echo "DRIFT: $rel differs from shared/video-delivery/videoDelivery.ts" >&2
      status=1
    else
      echo "ok: $rel"
    fi
  else
    mkdir -p "$(dirname "$target")"
    cp "$source_file" "$target"
    echo "synced: $rel"
  fi
done

if [[ "$mode" == "check" && $status -ne 0 ]]; then
  echo "run scripts/sync-video-delivery.sh to restore the copies" >&2
fi
exit $status

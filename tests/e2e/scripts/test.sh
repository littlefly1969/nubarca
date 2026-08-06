#!/usr/bin/env bash
# Run the browser matrix.
#
# Default runner is the official Playwright container. That is not incidental:
# WebKit's Linux build needs a specific set of system libraries (ICU 66,
# libwebp 6, libffi 7), which most host distributions do not ship and which
# cannot be installed without root. Running the engines in the image Playwright
# publishes for its own version makes the matrix reproducible on any host and
# removes "works on my distro" from the result.
#
# E2E_RUNNER=host runs the engines natively instead — faster, but only the
# engines your host can actually launch.
set -euo pipefail
. "$(dirname "${BASH_SOURCE[0]}")/lib.sh"

RUNNER="${E2E_RUNNER:-container}"
PLAYWRIGHT_VERSION="$(node -p "require('$E2E_ROOT/package.json').devDependencies['@playwright/test']")"
IMAGE="mcr.microsoft.com/playwright:v${PLAYWRIGHT_VERSION}-noble"

if [ "$RUNNER" = "host" ]; then
  say "running the browser matrix on the host"
  cd "$E2E_ROOT"
  exec npx playwright test "$@"
fi

say "running the browser matrix in $IMAGE"
docker image inspect "$IMAGE" >/dev/null 2>&1 || {
  info "pulling $IMAGE (first run)"
  docker pull "$IMAGE"
}

# --network host so the container reaches the dev server and API on the host's
# loopback. --ipc=host is Playwright's documented requirement for Chromium.
docker run --rm \
  --network host \
  --ipc=host \
  --user "$(id -u):$(id -g)" \
  -e HOME=/tmp \
  -e CI="${CI:-}" \
  -e E2E_WEB_URL="$E2E_WEB_URL" \
  -e E2E_API_URL="$E2E_API_URL" \
  -v "$REPO_ROOT":"$REPO_ROOT" \
  -w "$E2E_ROOT" \
  "$IMAGE" \
  npx playwright test "$@"

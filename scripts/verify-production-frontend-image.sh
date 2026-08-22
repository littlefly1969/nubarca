#!/usr/bin/env bash
# Verify a built production FRONTEND image WITHOUT deploying it.
#
# Same purpose as verify-production-image.sh does for the API: prove the image
# came from the source SHA it claims and that what it serves is what nginx is
# supposed to serve. The difference is that a frontend image's contract is
# mostly BEHAVIOUR, so this runs the container and makes real requests rather
# than only listing files. A `dist/` that copied cleanly and an nginx that
# answers correctly are not the same statement.
#
#   scripts/verify-production-frontend-image.sh <image-ref> <expected-git-sha>
#
# What it deliberately does NOT test: /tv.apk and /download/tv/*. Those are
# served from a volume the INSTALLATION mounts, never from the image — the same
# separation as /dev/dri for the backend. Asserting them here would either fail
# on a correct image or pass by accident, and both are worse than not asking.
set -euo pipefail

image="${1:?usage: verify-production-frontend-image.sh <image-ref> <expected-git-sha>}"
expected_sha="${2:?expected git sha is required}"

failures=0
pass() { printf '  ok    %s\n' "$1"; }
fail() { printf '  FAIL  %s\n' "$1"; failures=$((failures + 1)); }

container=""
cleanup() { [ -n "$container" ] && docker rm -f "$container" >/dev/null 2>&1 || true; }
trap cleanup EXIT

probe() { docker run --rm --entrypoint /bin/sh "$image" -c "$1" 2>/dev/null; }

# Capture, then match — never `probe ... | grep -q`.
#
# `grep -q` exits at the first match and closes the pipe; `docker run` then dies
# of SIGPIPE, and under `set -o pipefail` the pipeline reports FAILURE even
# though the match succeeded. Whether it bites depends on whether docker has
# finished writing first, so it shows up as an intermittent false failure on
# exactly the checks whose output takes longest to produce. Capturing into a
# variable removes the pipe, and with it the race.
contains() {  # contains <shell-command-inside-image> <needle>
  local out
  out="$(probe "$1")" || true
  [[ "$out" == *"$2"* ]]
}

echo "Verifying $image"
echo "  expected SHA  : $expected_sha"
echo

# --- provenance -------------------------------------------------------------
# The frontend carries its provenance as an OCI label rather than an env var:
# nginx has no use for an application variable, and a label is the standard
# place a build records what it came from.
revision="$(docker image inspect "$image" \
  --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' 2>/dev/null || true)"
if [ -z "$revision" ] || [ "$revision" = "<no value>" ]; then
  fail "org.opencontainers.image.revision is missing — the image cannot say what built it"
elif [ "$revision" = "$expected_sha" ]; then
  pass "org.opencontainers.image.revision == $expected_sha"
else
  fail "revision is $revision, expected $expected_sha"
fi

# --- the server itself ------------------------------------------------------
if contains 'command -v nginx' nginx; then
  pass "nginx present"
else
  fail "nginx missing"
fi

if contains 'nginx -t 2>&1' 'syntax is ok'; then
  pass "nginx -t accepts the shipped configuration"
else
  fail "nginx -t rejected the configuration"
fi

# --- the built application --------------------------------------------------
if [ "$(probe 'wc -c < /usr/share/nginx/html/index.html' || echo 0)" -gt 100 ]; then
  pass "index.html present and non-empty"
else
  fail "index.html missing or empty"
fi

# Vite emits content-hashed bundles. Requiring the HASH, not merely a .js file,
# is what distinguishes a real production build from a stray asset.
bundles="$(probe 'ls /usr/share/nginx/html/assets 2>/dev/null | grep -cE "^.+-[A-Za-z0-9_-]{8,}\.(js|css)$"' || echo 0)"
if [ "${bundles:-0}" -ge 2 ]; then
  pass "/assets carries $bundles content-hashed Vite bundles"
else
  fail "/assets has no recognisable hashed Vite bundles (found ${bundles:-0})"
fi

# And index.html must actually REFERENCE them, or the bundles are orphans.
if contains 'grep -qE "/assets/.+-[A-Za-z0-9_-]{8,}\.js" /usr/share/nginx/html/index.html && echo yes' yes; then
  pass "index.html references a hashed bundle"
else
  fail "index.html references no hashed bundle"
fi

# --- the build toolchain must not have travelled into the runtime -----------
for tool in node npm; do
  if contains "command -v $tool" "$tool"; then
    fail "$tool is present in the runtime image (build stage leaked)"
  else
    pass "no $tool in the runtime image"
  fi
done

# --- behaviour: the nginx contract, exercised ------------------------------
container="$(docker run -d -P "$image")"
port=""
for _ in $(seq 1 30); do
  port="$(docker port "$container" 80/tcp 2>/dev/null | head -1 | sed 's/.*://')"
  [ -n "$port" ] && curl -fsS -o /dev/null --max-time 2 "http://127.0.0.1:${port}/" 2>/dev/null && break
  sleep 1
done

if [ -z "$port" ]; then
  fail "the container never published a port — nginx did not start"
else
  base="http://127.0.0.1:${port}"
  code() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$1"; }

  [ "$(code "$base/")" = "200" ] \
    && pass "GET / -> 200" || fail "GET / -> $(code "$base/")"

  # SPA fallback: a client-side route that is not a file must still boot the app.
  spa="$(code "$base/albums/deep/link/that/is/not/a/file")"
  [ "$spa" = "200" ] \
    && pass "GET a client-side route -> 200 (SPA fallback)" \
    || fail "SPA deep link -> $spa, expected 200"

  # …but the fallback must NOT swallow missing assets. A hashed bundle that is
  # gone must 404, or a stale client silently receives HTML where it expected
  # JavaScript — which fails later, somewhere else, as a parse error.
  missing="$(code "$base/assets/does-not-exist-000000.js")"
  [ "$missing" = "404" ] \
    && pass "GET a missing /assets file -> 404 (no SPA fallback)" \
    || fail "missing asset -> $missing, expected 404"

  # The real bundle it advertises must actually be served.
  asset="$(docker run --rm --entrypoint /bin/sh "$image" -c \
    'grep -oE "/assets/[^\"]+\.js" /usr/share/nginx/html/index.html | head -1' 2>/dev/null || true)"
  if [ -n "$asset" ]; then
    got="$(code "$base$asset")"
    [ "$got" = "200" ] \
      && pass "GET $asset -> 200" || fail "GET $asset -> $got"
  else
    fail "could not determine a bundle URL from index.html"
  fi
fi

echo
if [ "$failures" -eq 0 ]; then
  echo "FRONTEND IMAGE VERIFIED"
  echo "Image: $image"
  echo "org.opencontainers.image.revision: $revision"
  exit 0
fi
echo "FRONTEND IMAGE VERIFICATION FAILED ($failures check(s))"
exit 1

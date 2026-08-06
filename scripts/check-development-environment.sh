#!/usr/bin/env bash
# NubArca — read-only development environment diagnostics.
#
# Reports the toolchain versions this workstation actually has and compares
# them with the canonical versions declared by the repository
# (see docs/development-environment.md for the authoritative matrix).
#
# This script is STRICTLY read-only. It never installs, downloads, upgrades,
# writes a file, starts a container, or reads `.env` / any secret. Running it
# must leave the working tree byte-identical.
#
# Exit status:
#   0  every REQUIRED prerequisite is present and in range
#   1  at least one REQUIRED prerequisite is missing or out of range
#
# Missing OPTIONAL tools are reported but never fail the run: a backend-only
# contributor legitimately has no Android SDK, and a frontend-only contributor
# legitimately has no FFmpeg.
#
# Usage:
#   scripts/check-development-environment.sh            # core + optional
#   scripts/check-development-environment.sh --quiet    # only problems

set -uo pipefail   # NOTE: deliberately no `-e` — a missing optional tool must
                   # not abort the report.

quiet=0
case "${1:-}" in
  --quiet) quiet=1 ;;
  -h|--help)
    sed -n '2,24p' "$0" | sed 's/^# \{0,1\}//'
    exit 0
    ;;
  '') ;;
  *)
    printf 'unknown argument: %s (try --help)\n' "$1" >&2
    exit 2
    ;;
esac

# --- output helpers -----------------------------------------------------------
if [ -t 1 ]; then
  c_ok=$'\033[32m'; c_bad=$'\033[31m'; c_warn=$'\033[33m'
  c_dim=$'\033[2m'; c_off=$'\033[0m'
else
  c_ok=''; c_bad=''; c_warn=''; c_dim=''; c_off=''
fi

required_failures=0

# row STATUS NAME FOUND EXPECTED NOTE
row() {
  local status="$1" name="$2" found="$3" expected="$4" note="${5:-}"
  local mark color
  case "$status" in
    ok)      mark='OK  '; color="$c_ok" ;;
    missing) mark='MISS'; color="$c_bad" ;;
    range)   mark='DRIFT'; color="$c_bad" ;;
    opt)     mark='----'; color="$c_dim" ;;
    info)    mark='NOTE'; color="$c_warn" ;;
  esac
  if [ "$quiet" -eq 1 ] && { [ "$status" = ok ] || [ "$status" = opt ]; }; then
    return 0
  fi
  printf '%s%-5s%s %-22s %-26s %s%s%s\n' \
    "$color" "$mark" "$c_off" "$name" "$found" "$c_dim" "$expected" "$c_off"
  [ -n "$note" ] && printf '      %s%s%s\n' "$c_dim" "$note" "$c_off"
  return 0
}

have() { command -v "$1" >/dev/null 2>&1; }

# major_ge <version-string> <min-major>  -> 0 when major(version) >= min-major
major_ge() {
  local major="${1%%.*}"
  case "$major" in ''|*[!0-9]*) return 1 ;; esac
  [ "$major" -ge "$2" ]
}

# ver_ge <a> <b>  -> 0 when a >= b (dotted numeric compare)
ver_ge() {
  [ "$1" = "$2" ] && return 0
  local lowest
  lowest="$(printf '%s\n%s\n' "$1" "$2" | sort -V | head -n1)"
  [ "$lowest" = "$2" ]
}

# check_required <name> <cmd> <expected-label> <version-extractor> <predicate>
check_tool() {
  local kind="$1" name="$2" cmd="$3" expected="$4" version="$5" ok="$6" note="${7:-}"
  if [ -z "$version" ]; then
    if [ "$kind" = required ]; then
      row missing "$name" "not installed" "$expected" "$note"
      required_failures=$((required_failures + 1))
    else
      row opt "$name" "not installed (optional)" "$expected" "$note"
    fi
    return
  fi
  if [ "$ok" = yes ]; then
    row ok "$name" "$version" "$expected"
  elif [ "$kind" = required ]; then
    row range "$name" "$version" "$expected" "$note"
    required_failures=$((required_failures + 1))
  else
    row info "$name" "$version" "$expected" "$note"
  fi
}

repo_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"

printf '%sNubArca development environment%s  (%s)\n' "$c_dim" "$c_off" "$repo_dir"
printf '%scanonical matrix: docs/development-environment.md%s\n\n' "$c_dim" "$c_off"
printf '%s%-5s %-22s %-26s %s%s\n' "$c_dim" "STAT" "TOOL" "FOUND" "EXPECTED" "$c_off"

# --- core (required for every kind of contribution) ---------------------------
printf '\n%s# core%s\n' "$c_dim" "$c_off"

v=''; have bash && v="${BASH_VERSION%%(*}"
check_tool required "bash" bash ">= 4.0" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 4; then echo yes; else echo no; fi)" \
  "the repo scripts use bash arrays and \`set -o pipefail\`"

v=''; have git && v="$(git --version 2>/dev/null | awk '{print $3}')"
check_tool required "git" git ">= 2.30" "$v" \
  "$(if [ -n "$v" ] && ver_ge "$v" 2.30; then echo yes; else echo no; fi)"

# --- backend ------------------------------------------------------------------
printf '\n%s# backend (.NET / ASP.NET Core)%s\n' "$c_dim" "$c_off"

v=''; have dotnet && v="$(cd "$repo_dir" && dotnet --version 2>/dev/null)"
check_tool required ".NET SDK" dotnet "10.0.104+ (10.0.1xx)" "$v" \
  "$(if [ -n "$v" ] && ver_ge "$v" 10.0.104; then echo yes; else echo no; fi)" \
  "pinned by global.json (rollForward=latestFeature)"

v=''
if have dotnet; then
  v="$(dotnet --list-runtimes 2>/dev/null \
        | awk '$1=="Microsoft.AspNetCore.App"{print $2}' | sort -V | tail -n1)"
fi
check_tool required "ASP.NET runtime" dotnet "10.0.x" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 10; then echo yes; else echo no; fi)"

# --- frontend + TV (shared Node toolchain) ------------------------------------
printf '\n%s# frontend + TV (Node)%s\n' "$c_dim" "$c_off"

v=''; have node && v="$(node --version 2>/dev/null | tr -d v)"
check_tool required "Node.js" node ">= 22.13 (see .nvmrc)" "$v" \
  "$(if [ -n "$v" ] && ver_ge "$v" 22.13.0; then echo yes; else echo no; fi)" \
  "Expo SDK 56 requires >= 22.13; Vite 7 requires >= 22.12"

v=''; have npm && v="$(npm --version 2>/dev/null)"
check_tool required "npm" npm ">= 10" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 10; then echo yes; else echo no; fi)"

# --- TV native build (optional) -----------------------------------------------
printf '\n%s# TV native APK build (optional — only to build an APK)%s\n' "$c_dim" "$c_off"

v=''
if have java; then
  # `java -version` prints e.g.  openjdk version "17.0.19" 2026-04-21
  v="$(java -version 2>&1 | head -n1 | sed -E 's/^[^"]*"([^"]+)".*$/\1/')"
fi
# RN 0.85 / Gradle 9.3.x: JDK 17 and 21 work, 26 breaks the foojay resolver.
jdk_ok=no
case "${v%%.*}" in 17|21) jdk_ok=yes ;; esac
check_tool optional "JDK (Android)" java "17 or 21 — NOT 26" "$v" "$jdk_ok" \
  "JDK 26 breaks the Gradle foojay toolchain resolver (see tv/README.md)"

android_sdk="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-}}"
if [ -n "$android_sdk" ] && [ -d "$android_sdk" ]; then
  row ok "Android SDK" "$android_sdk" "android-36 / build-tools 36"
elif have adb; then
  row info "Android SDK" "adb present, env unset" "ANDROID_HOME / ANDROID_SDK_ROOT" \
    "export ANDROID_HOME=\$HOME/Android/Sdk before ./gradlew"
else
  row opt "Android SDK" "not installed (optional)" "android-36 / build-tools 36"
fi

# --- containers / database (optional) -----------------------------------------
printf '\n%s# containers, database, media (optional per area)%s\n' "$c_dim" "$c_off"

v=''; have docker && v="$(docker version --format '{{.Client.Version}}' 2>/dev/null)"
[ -z "$v" ] && have docker && v="$(docker --version 2>/dev/null | awk '{print $3}' | tr -d ,)"
check_tool optional "Docker Engine" docker ">= 24" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 24; then echo yes; else echo no; fi)" \
  "needed for the Postgres dev container and for building images"

v=''; have docker && v="$(docker compose version --short 2>/dev/null)"
check_tool optional "Docker Compose" docker ">= 2.20 (compose plugin)" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 2; then echo yes; else echo no; fi)"

v=''; have ffmpeg && v="$(ffmpeg -version 2>/dev/null | head -n1 | awk '{print $3}' | tr -d n)"
check_tool optional "FFmpeg" ffmpeg ">= 6 (posters, HLS)" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 6; then echo yes; else echo no; fi)" \
  "only needed to run poster/HLS derivative code paths locally"

v=''; have ffprobe && v="$(ffprobe -version 2>/dev/null | head -n1 | awk '{print $3}' | tr -d n)"
check_tool optional "FFprobe" ffprobe ">= 6 (video metadata)" "$v" \
  "$(if [ -n "$v" ] && major_ge "$v" 6; then echo yes; else echo no; fi)"

# --- deploy-only helpers ------------------------------------------------------
printf '\n%s# deploy-only helpers (optional on a dev workstation)%s\n' "$c_dim" "$c_off"

v=''; have jq && v="$(jq --version 2>/dev/null | tr -d 'jq-')"
check_tool optional "jq" jq "any" "$v" "$(if [ -n "$v" ]; then echo yes; else echo no; fi)" \
  "used only by deploy/backup.sh and deploy/restore.sh"

v=''; have rsync && v="$(rsync --version 2>/dev/null | head -n1 | awk '{print $3}')"
check_tool optional "rsync" rsync "any" "$v" "$(if [ -n "$v" ]; then echo yes; else echo no; fi)"

v=''; have curl && v="$(curl --version 2>/dev/null | head -n1 | awk '{print $2}')"
check_tool optional "curl" curl "any" "$v" "$(if [ -n "$v" ]; then echo yes; else echo no; fi)"

# --- verdict ------------------------------------------------------------------
printf '\n'
if [ "$required_failures" -eq 0 ]; then
  printf '%sAll required prerequisites satisfied.%s\n' "$c_ok" "$c_off"
  printf '%sOptional gaps above only limit the corresponding area.%s\n' "$c_dim" "$c_off"
  exit 0
fi
printf '%s%d required prerequisite(s) missing or out of range.%s\n' \
  "$c_bad" "$required_failures" "$c_off"
printf '%sSee docs/development-environment.md for install instructions.%s\n' "$c_dim" "$c_off"
exit 1

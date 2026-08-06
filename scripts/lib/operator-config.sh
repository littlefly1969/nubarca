#!/usr/bin/env bash
# Shared validation for operator-supplied NubArca deployment configuration.
#
# Source this from any script that touches an installation:
#
#   . "$(dirname "${BASH_SOURCE[0]}")/../scripts/lib/operator-config.sh"
#   require_production_ssh
#   require_production_checkout
#
# Why this exists
# ---------------
# NubArca source describes the product, never one installation. A host name, an
# IP address, a login, a checkout directory, a storage mount and a public origin
# are all properties of a particular deployment, so they arrive through the
# environment and are validated here — in one place — instead of being repeated
# as a plausible-looking default in each script.
#
# Every helper FAILS CLOSED. A default would be worse than an error: the failure
# mode we are guarding against is a script that quietly deploys to, publishes to,
# or backs up the wrong machine because an unset variable fell back to whatever
# the author's own installation happened to be called.
#
# Required by essentially every production operation:
#
#   NUBARCA_PRODUCTION_SSH        ssh destination, e.g. user@host
#   NUBARCA_PRODUCTION_CHECKOUT   absolute path of the deployment checkout
#
# Required where the specific operation needs them:
#
#   NUBARCA_PUBLIC_ORIGIN            externally reachable https:// origin
#   NUBARCA_STORAGE_ROOT             blob storage root
#   NUBARCA_SERVICE_ROOT             service data / model root
#   NUBARCA_IMPORT_ROOT              bulk import source root
#   NUBARCA_TV_APK_DIR               directory the TV APK is published into
#   NUBARCA_ENCRYPTED_BACKUP_TARGET  directory encrypted backups are written to
set -uo pipefail

# Print the reason and exit non-zero. Callers are operational scripts, so the
# message names the variable and what a valid value looks like.
operator_config_fail() {
  printf 'operator configuration error: %s\n' "$1" >&2
  exit 78 # EX_CONFIG
}

_operator_value() {
  # Indirect expansion, tolerant of `set -u`.
  local name="$1"
  printf '%s' "${!name:-}"
}

require_operator_var() {
  local name="$1" description="${2:-}"
  local value
  value="$(_operator_value "$name")"
  if [[ -z "$value" ]]; then
    operator_config_fail "$name is required${description:+ ($description)}. Obtain it from the operator; it is never inferred and never defaulted."
  fi
}

require_absolute_path_var() {
  local name="$1" description="${2:-}"
  require_operator_var "$name" "$description"
  local value
  value="$(_operator_value "$name")"
  if [[ "$value" != /* ]]; then
    operator_config_fail "$name must be an absolute path, got: $value"
  fi
  if [[ "$value" == *..* ]]; then
    operator_config_fail "$name must not contain '..', got: $value"
  fi
  if [[ ! "$value" =~ ^/[A-Za-z0-9._/@+-]*$ ]]; then
    operator_config_fail "$name contains characters that are unsafe to pass to a remote shell: $value"
  fi
}

require_existing_local_dir_var() {
  local name="$1" description="${2:-}"
  require_absolute_path_var "$name" "$description"
  local value
  value="$(_operator_value "$name")"
  [[ -d "$value" ]] || operator_config_fail "$name does not exist or is not a directory: $value"
}

# The ssh destination. Accepts user@host, host, or a configured ssh alias, and
# rejects anything that could be read as an option or smuggle a second argument.
require_production_ssh() {
  require_operator_var NUBARCA_PRODUCTION_SSH "ssh destination for the installation, e.g. user@host"
  local value="$NUBARCA_PRODUCTION_SSH"
  if [[ "$value" == -* ]]; then
    operator_config_fail "NUBARCA_PRODUCTION_SSH must not begin with '-': $value"
  fi
  if [[ ! "$value" =~ ^([A-Za-z0-9._-]+@)?[A-Za-z0-9._-]+$ ]]; then
    operator_config_fail "NUBARCA_PRODUCTION_SSH must be [user@]host or an ssh alias, got: $value"
  fi
}

require_public_origin() {
  require_operator_var NUBARCA_PUBLIC_ORIGIN "the externally reachable public origin of the installation"
  local value="${NUBARCA_PUBLIC_ORIGIN%/}"
  if [[ "$value" != https://* ]]; then
    operator_config_fail "NUBARCA_PUBLIC_ORIGIN must be an https:// origin (production is never plain http), got: $value"
  fi
  if [[ "$value" =~ [[:space:]] ]]; then
    operator_config_fail "NUBARCA_PUBLIC_ORIGIN must not contain whitespace: $value"
  fi
  # Normalised, so callers can concatenate a path without doubling the slash.
  NUBARCA_PUBLIC_ORIGIN="$value"
  export NUBARCA_PUBLIC_ORIGIN
}

# The checkout path itself. Shape only — `require_remote_checkout` proves it is
# really a NubArca worktree on the far end.
require_production_checkout() {
  require_absolute_path_var NUBARCA_PRODUCTION_CHECKOUT "absolute path of the deployment checkout on the installation host"
}

# Prove the remote path is a git worktree whose origin is the active NubArca
# repository. Call after require_production_ssh + require_production_checkout.
#
# `expected_remote_substring` defaults to the repository name, so a checkout
# still pointing at some other origin is refused before anything is deployed.
require_remote_checkout() {
  local expected_remote_substring="${1:-nubarca}"
  require_production_ssh
  require_production_checkout

  local probe
  if ! probe="$(ssh -o BatchMode=yes "$NUBARCA_PRODUCTION_SSH" \
    "cd '$NUBARCA_PRODUCTION_CHECKOUT' 2>/dev/null && git rev-parse --is-inside-work-tree 2>/dev/null && git remote get-url origin 2>/dev/null")"; then
    operator_config_fail "cannot verify $NUBARCA_PRODUCTION_CHECKOUT on $NUBARCA_PRODUCTION_SSH: it must exist and be a git worktree"
  fi
  if [[ "$(sed -n 1p <<<"$probe")" != "true" ]]; then
    operator_config_fail "$NUBARCA_PRODUCTION_CHECKOUT on $NUBARCA_PRODUCTION_SSH is not a git worktree"
  fi
  local remote
  remote="$(sed -n 2p <<<"$probe")"
  if [[ "$remote" != *"$expected_remote_substring"* ]]; then
    operator_config_fail "the checkout's origin remote is not the active NubArca repository (expected it to contain '$expected_remote_substring')"
  fi
}

require_storage_root() {
  require_absolute_path_var NUBARCA_STORAGE_ROOT "content-addressed blob storage root on the installation host"
}

require_service_root() {
  require_absolute_path_var NUBARCA_SERVICE_ROOT "service data and model root on the installation host"
}

require_import_root() {
  require_absolute_path_var NUBARCA_IMPORT_ROOT "bulk import source root on the installation host"
}

require_tv_apk_dir() {
  require_absolute_path_var NUBARCA_TV_APK_DIR "directory the signed TV APK is published into on the installation host"
}

require_encrypted_backup_target() {
  require_absolute_path_var NUBARCA_ENCRYPTED_BACKUP_TARGET "directory encrypted backups are written to on the installation host"
}

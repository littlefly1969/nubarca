#!/usr/bin/env bash
# Installs the Print Agent update this box's owner or administrator asked for.
# Started as root by nubarca-print-agent-update.path when the agent, idle
# between jobs, writes update-request.json into its state directory.
#
# Nothing the agent account wrote decides what is installed: that file only
# rings the bell. This script asks the server itself, with the station
# credential and the agent binary already installed, which bundle to fetch,
# and checks it against the size and SHA-256 the server names (the server
# offers only the exact bundle the request pinned). The new bundle replaces
# /opt/nubarca-print-agent only after its binary answers with the offered
# version, and stays only once the restarted agent has reached the server
# again.
#
# From the moment the installed bundle is first moved, ANY way this script
# ends short of that health check — the new agent never reaching the server,
# a failed unit install or daemon-reload, an error, a signal — puts the
# previous bundle and its system files back and restarts it (restore, run by
# the EXIT trap while the swap is armed).
#
# While it runs it keeps /run/nubarca-print-agent-update/in-progress, and the
# agent takes no job while that marker exists: the pause lasts exactly as long
# as this service, not a timer of its own. /run is emptied at boot, so a power
# loss mid-update leaves no pause behind.
#
# It installs only on a request it claimed: it renames update-request.json to
# update-request.claimed after raising the marker. The agent takes a request
# nobody picked up back with a rename too, so exactly one of the two wins: an
# agent that went back to work is never updated under a job.
#
# The outcome, "ok|failed <version> <request id>", is left in update-result for
# the agent to relay on its next heartbeat.
set -euo pipefail
export LC_ALL=C
umask 0022

# Tests run this script against a scratch tree (deploy/tests/printBoxUpdate.test.mjs);
# on a box it is always /.
root="${NUBARCA_PRINT_UPDATE_TEST_ROOT:-}"
instance='box'
user="nubarca-print-$instance"
unit="nubarca-print-agent@$instance.service"
install_dir="$root/opt/nubarca-print-agent"
next_dir="$install_dir.new"
previous_dir="$install_dir.previous"
state_dir="$root/var/lib/nubarca-print-agent/$instance"
config_path="$root/etc/nubarca-print-agent/$instance.json"
staging="$root/var/lib/nubarca-print-agent-update"
run_dir="$root/run/nubarca-print-agent-update"
in_progress="$run_dir/in-progress"
systemd_dir="$root/etc/systemd/system"
cups_backend="$root/usr/lib/cups/backend/nubarca-sim"
polkit_rule="$root/etc/polkit-1/rules.d/49-nubarca-print-box.rules"
request="$state_dir/update-request.json"
claimed="$state_dir/update-request.claimed"
result_name='update-result'
health_seconds="${NUBARCA_PRINT_UPDATE_HEALTH_SECONDS:-180}"
poll_seconds="${NUBARCA_PRINT_UPDATE_POLL_SECONDS:-3}"

log() { printf 'nubarca-print-agent-update: %s\n' "$*"; }

[[ -n "$root" || $EUID -eq 0 ]] || { echo 'Run as root (the update service does).' >&2; exit 1; }
[[ -d "$state_dir" && ! -L "$state_dir" ]] || { log "no state directory $state_dir"; exit 1; }

install -d -m 0755 "$run_dir"
exec 9>"$run_dir/lock"
flock -n 9 || { log 'another update is running'; exit 0; }
: > "$in_progress"
chmod 0644 "$in_progress"

version=''
request_id=''
# Set just before the installed bundle is first moved; cleared only once the
# new agent has reached the server.
rollback_armed=false

# Written into the agent's own directory: a fresh file renamed over the old
# name, so a link the agent account left there is replaced, never followed.
report() {
  local outcome="$1" temporary
  [[ -n "$version" && -n "$request_id" ]] || return 0
  temporary="$(mktemp "$state_dir/.update-result.XXXXXX")"
  printf '%s %s %s\n' "$outcome" "$version" "$request_id" > "$temporary"
  chmod 0644 "$temporary"
  mv -fT -- "$temporary" "$state_dir/$result_name"
  log "$outcome $version"
}

# The units, CUPS backend and polkit rule of whichever bundle is in place.
install_system_files() {
  local linux="$install_dir/linux" name
  install -m 0644 "$linux/nubarca-print-agent@.service" "$systemd_dir/nubarca-print-agent@.service"
  for name in nubarca-print-agent-update.path nubarca-print-agent-update.service; do
    if [[ -f "$linux/$name" ]]; then install -m 0644 "$linux/$name" "$systemd_dir/$name"; fi
  done
  install -m 0755 "$linux/nubarca-sim" "$cups_backend"
  if [[ -f "$polkit_rule" ]]; then
    install -m 0644 "$linux/nubarca-print-box.rules" "$polkit_rule"
  fi
  chmod 0755 "$install_dir/NubArca.PrintAgent" "$linux"/*.sh "$linux/nubarca-sim"
  systemctl daemon-reload
}

# Best effort, step by step: every step that can still help is tried.
# shellcheck disable=SC2317  # run from the EXIT trap
restore() {
  set +e
  log "restoring the previous agent"
  systemctl stop "$unit"
  if [[ -d "$previous_dir" ]]; then
    if [[ -e "$install_dir" ]]; then
      rm -rf -- "$install_dir.failed"
      mv -T -- "$install_dir" "$install_dir.failed"
    fi
    mv -T -- "$previous_dir" "$install_dir"
  fi
  install_system_files
  systemctl reset-failed "$unit"
  systemctl start "$unit"
  rm -rf -- "$install_dir.failed"
  report failed
}

# shellcheck disable=SC2317  # run by the EXIT trap
on_exit() {
  local status=$?
  trap - EXIT
  if [[ "$rollback_armed" == true ]]; then
    rollback_armed=false
    restore
    status=1
  fi
  # The claimed bell is always taken down, whatever happens.
  rm -f -- "$claimed"
  rm -rf -- "$staging" "$next_dir"
  rm -f -- "$in_progress"
  exit "$status"
}
trap on_exit EXIT
# A signal ends the script through the EXIT trap, and so through restore.
trap 'exit 143' TERM
trap 'exit 130' INT
trap 'exit 129' HUP

# The restarted agent reached the server: it wrote this version after a heartbeat.
healthy() {
  local deadline=$((SECONDS + health_seconds)) marker="$state_dir/heartbeat-ok" since="$1" seen
  while (( SECONDS < deadline )); do
    if [[ -f "$marker" && ! -L "$marker" ]] && (( $(stat -c %Y "$marker") >= since )); then
      seen="$(head -c 32 "$marker" | tr -d '\n')"
      [[ "$seen" == "$version" ]] && return 0
    fi
    systemctl is-failed --quiet "$unit" && return 1
    sleep "$poll_seconds"
  done
  return 1
}

# The claim: one atomic rename, after the marker is up. If the agent took its
# request back first, there is nothing to install.
if ! mv -T -- "$request" "$claimed" 2>/dev/null; then
  log 'no update request to pick up (the agent took it back)'
  exit 0
fi
# The request names what the agent asked for. It decides nothing that is
# installed; the offer must match it, and a failure is reported against it.
if [[ -f "$claimed" && ! -L "$claimed" ]]; then
  read -r version request_id < <(python3 -I - "$claimed" <<'PY' || true
import json, re, sys
try:
    with open(sys.argv[1]) as f:
        request = json.loads(f.read(512))
    version, request_id = str(request["version"]), str(request["requestId"])
    if re.fullmatch(r"\d{1,4}\.\d{1,4}\.\d{1,4}", version) and re.fullmatch(r"\d{1,20}", request_id):
        print(version, request_id)
except Exception:
    pass
PY
  ) || true
fi
[[ -n "$version" && -n "$request_id" ]] || { log 'the update request is not valid; nothing is installed'; exit 1; }

# --- 1. What the server offers, fetched and checked by the installed agent ----------
rm -rf -- "$staging"
install -d -m 0700 "$staging"
set +e
(cd "$install_dir" && DOTNET_ENVIRONMENT=Production "$install_dir/NubArca.PrintAgent" \
  self-update-fetch --config "$config_path" --output "$staging")
fetched=$?
set -e
case "$fetched" in
  0) ;;
  3) log 'nothing is offered (withdrawn, superseded, or already installed)'; exit 0 ;;
  *) log "the update could not be fetched (exit $fetched)"; report failed; exit 1 ;;
esac

offer_fields="$(python3 -I - "$staging/offer.json" <<'PY'
import json, re, sys
offer = json.load(open(sys.argv[1]))
version, name, request_id = offer["version"], offer["fileName"], offer["requestId"]
assert re.fullmatch(r"\d{1,4}\.\d{1,4}\.\d{1,4}", version)
assert name == f"nubarca-print-agent-{version}-linux-x64.zip"
assert re.fullmatch(r"\d{1,20}", request_id)
print(version, name, request_id)
PY
)"
read -r offer_version file_name offer_request_id <<<"$offer_fields"
if [[ "$offer_version" != "$version" || "$offer_request_id" != "$request_id" ]]; then
  log "the server offers $offer_version (request $offer_request_id), not what the agent asked for"
  report failed; exit 1
fi
log "installing $version"

# --- 2. The new bundle, beside the current one ---------------------------------------
rm -rf -- "$next_dir"
install -d -m 0755 "$next_dir"
if ! python3 -I - "$staging/$file_name" "$next_dir" <<'PY'
import os, sys, zipfile
archive, target = sys.argv[1], os.path.realpath(sys.argv[2])
with zipfile.ZipFile(archive) as z:
    for member in z.infolist():
        path = os.path.realpath(os.path.join(target, member.filename))
        if path != target and not path.startswith(target + os.sep):
            sys.exit(f"unsafe path in bundle: {member.filename!r}")
    z.extractall(target)
PY
then
  report failed; exit 1
fi
chmod -R u+rwX,go+rX,go-w "$next_dir"
for required in NubArca.PrintAgent linux/update-print-box.sh linux/nubarca-print-agent@.service \
  linux/nubarca-print-agent-update.path linux/nubarca-print-agent-update.service linux/nubarca-sim \
  linux/nubarca-print-box.rules; do
  [[ -f "$next_dir/$required" ]] || { log "the bundle has no $required"; report failed; exit 1; }
done
chmod 0755 "$next_dir/NubArca.PrintAgent"
# The new binary runs, as the agent's own account, and is the version offered.
staged_version="$( (cd "$next_dir"; timeout 60 runuser -u "$user" -- "$next_dir/NubArca.PrintAgent" version 2>/dev/null) \
  | head -n 1 || true)"
if [[ "$staged_version" != "$version" ]]; then
  log "the staged agent reports '${staged_version:-nothing}', not $version"
  report failed; exit 1
fi

# --- 3. Swap, restart, and keep it only if it reaches the server ----------------------
systemctl stop "$unit"
rm -rf -- "$previous_dir"
rollback_armed=true
mv -T -- "$install_dir" "$previous_dir"
mv -T -- "$next_dir" "$install_dir"
install_system_files
started="$(date +%s)"
systemctl start "$unit" || true

if ! healthy "$started"; then
  log "$version did not reach the server"
  exit 1
fi
rollback_armed=false
report ok

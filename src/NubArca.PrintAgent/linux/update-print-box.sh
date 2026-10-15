#!/usr/bin/env bash
# Installs the Print Agent update this box's owner or administrator asked for.
# Started as root by nubarca-print-agent-update.path when the agent, idle
# between jobs, writes update-request.json into its state directory.
#
# Nothing the agent account wrote decides what is installed: that file only
# rings the bell. This script asks the server itself, with the station
# credential and the agent binary already installed, which bundle to fetch,
# and checks it against the size and SHA-256 the server names. The new bundle
# replaces /opt/nubarca-print-agent only after its binary answers with the
# offered version, and stays only once the restarted agent has reached the
# server again; otherwise the previous bundle comes back.
#
# The outcome, "ok|failed <version> <request id>", is left in update-result for
# the agent to relay on its next heartbeat.
set -euo pipefail
export LC_ALL=C
umask 0022

instance='box'
user="nubarca-print-$instance"
unit="nubarca-print-agent@$instance.service"
install_dir='/opt/nubarca-print-agent'
next_dir="$install_dir.new"
previous_dir="$install_dir.previous"
state_dir="/var/lib/nubarca-print-agent/$instance"
config_path="/etc/nubarca-print-agent/$instance.json"
staging='/var/lib/nubarca-print-agent-update'
request="$state_dir/update-request.json"
result_name='update-result'
health_seconds="${NUBARCA_PRINT_UPDATE_HEALTH_SECONDS:-180}"

log() { printf 'nubarca-print-agent-update: %s\n' "$*"; }

[[ $EUID -eq 0 ]] || { echo 'Run as root (the update service does).' >&2; exit 1; }
[[ -d "$state_dir" && ! -L "$state_dir" ]] || { log "no state directory $state_dir"; exit 1; }

exec 9>/run/nubarca-print-agent-update.lock
flock -n 9 || { log 'another update is running'; exit 0; }

version=''
request_id=''
# The bell is always taken down, whatever happens: the path unit would
# otherwise start this service again at once.
# shellcheck disable=SC2317  # run by the EXIT trap
cleanup() {
  rm -f -- "$request"
  rm -rf -- "$staging" "$next_dir"
}
trap cleanup EXIT

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
  local linux="$install_dir/linux"
  install -m 0644 "$linux/nubarca-print-agent@.service" /etc/systemd/system/nubarca-print-agent@.service
  local name
  for name in nubarca-print-agent-update.path nubarca-print-agent-update.service; do
    [[ -f "$linux/$name" ]] && install -m 0644 "$linux/$name" "/etc/systemd/system/$name"
  done
  install -m 0755 -o root -g root "$linux/nubarca-sim" /usr/lib/cups/backend/nubarca-sim
  if [[ -f /etc/polkit-1/rules.d/49-nubarca-print-box.rules ]]; then
    install -m 0644 "$linux/nubarca-print-box.rules" /etc/polkit-1/rules.d/49-nubarca-print-box.rules
  fi
  chmod 0755 "$install_dir/NubArca.PrintAgent" "$linux"/*.sh "$linux/nubarca-sim"
  systemctl daemon-reload
}

# The restarted agent reached the server: it wrote this version after a heartbeat.
healthy() {
  local deadline=$((SECONDS + health_seconds)) marker="$state_dir/heartbeat-ok" since="$1" seen
  while (( SECONDS < deadline )); do
    if [[ -f "$marker" && ! -L "$marker" ]] && (( $(stat -c %Y "$marker") >= since )); then
      seen="$(head -c 32 "$marker" | tr -d '\n')"
      [[ "$seen" == "$version" ]] && return 0
    fi
    systemctl is-failed --quiet "$unit" && return 1
    sleep 3
  done
  return 1
}

# The request names what the agent was offered. It decides nothing here; it
# only lets a fetch that fails be reported against the right request.
if [[ -f "$request" && ! -L "$request" ]]; then
  read -r version request_id < <(python3 -I - "$request" <<'PY' || true
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

# --- 1. What the server offers, fetched and checked by the installed agent ----------
rm -rf -- "$staging"
install -d -o root -g root -m 0700 "$staging"
set +e
(cd "$install_dir" && DOTNET_ENVIRONMENT=Production "$install_dir/NubArca.PrintAgent" \
  self-update-fetch --config "$config_path" --output "$staging")
fetched=$?
set -e
case "$fetched" in
  0) ;;
  3) log 'nothing is offered (withdrawn, or already installed)'; exit 0 ;;
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
read -r version file_name request_id <<<"$offer_fields"
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
mv -T -- "$install_dir" "$previous_dir"
mv -T -- "$next_dir" "$install_dir"
install_system_files
started="$(date +%s)"
systemctl start "$unit" || true

if healthy "$started"; then
  report ok
  exit 0
fi

log "$version did not reach the server; restoring the previous agent"
systemctl stop "$unit" || true
rm -rf -- "$install_dir.failed"
mv -T -- "$install_dir" "$install_dir.failed"
mv -T -- "$previous_dir" "$install_dir"
install_system_files
systemctl reset-failed "$unit" 2>/dev/null || true
systemctl start "$unit" || true
rm -rf -- "$install_dir.failed"
report failed
exit 1

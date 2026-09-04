#!/usr/bin/env bash
# Installs one isolated Linux fake-print station from the self-contained bundle.
# Run this only from /opt/nubarca-print-agent/linux as root. The enrollment token
# is read silently from stdin/TTY and is never written into config or arguments.
set -euo pipefail

usage() {
  cat <<'EOF'
Usage: sudo ./install-fake-instance.sh --instance <name> --server <https-origin> --station <guid>

Creates one systemd instance named nubarca-print-agent@<name>. Each instance
gets a distinct Unix account and a private /var/lib state directory. The
enrollment token is prompted for once and is not retained in shell history.
EOF
}

instance=''
server=''
station=''
while [[ $# -gt 0 ]]; do
  case "$1" in
    --instance) instance="${2:-}"; shift 2 ;;
    --server) server="${2:-}"; shift 2 ;;
    --station) station="${2:-}"; shift 2 ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo 'Run as root via sudo.' >&2; exit 1; }
[[ "$instance" =~ ^[a-z0-9][a-z0-9-]{0,15}$ ]] || { echo 'Instance must be 1–16 lowercase letters, digits or hyphens.' >&2; exit 2; }
[[ "$server" =~ ^https://[^[:space:]\"]+$ ]] || { echo 'Server must be an https origin without spaces.' >&2; exit 2; }
[[ "$station" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || { echo 'Station must be a GUID.' >&2; exit 2; }

script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
install_dir="$(dirname "$script_dir")"
[[ "$install_dir" == '/opt/nubarca-print-agent' ]] || { echo 'Extract the Linux bundle exactly to /opt/nubarca-print-agent.' >&2; exit 1; }
agent="$install_dir/NubArca.PrintAgent"
[[ -x "$agent" ]] || { echo "Missing executable: $agent" >&2; exit 1; }

user="nubarca-print-$instance"
state_dir="/var/lib/nubarca-print-agent/$instance"
config_dir='/etc/nubarca-print-agent'
config_path="$config_dir/$instance.json"
unit="nubarca-print-agent@$instance.service"

if systemctl list-unit-files --no-legend "$unit" | grep -q .; then
  echo "Service $unit already exists. Uninstall it before reinstalling." >&2
  exit 1
fi
if ! id "$user" >/dev/null 2>&1; then
  useradd --system --user-group --home-dir "$state_dir" --shell /usr/sbin/nologin "$user"
fi
install -d -o "$user" -g "$user" -m 0700 "$state_dir" "$state_dir/temp" "$state_dir/fake-output"
# The directory is shared by every instance. It contains no secrets and must
# remain traversable after later installs; each JSON file below is still
# readable only by root and its own instance group.
install -d -o root -g root -m 0755 "$config_dir"

umask 0077
cat > "$config_path" <<EOF
{
  "PrintAgent": {
    "ServerOrigin": "${server%/}",
    "CredentialPath": "${state_dir}/credential.bin",
    "JournalPath": "${state_dir}/journal.db",
    "TemporaryPath": "${state_dir}/temp",
    "Adapter": "fake",
    "PrinterName": null,
    "FakeOutputPath": "${state_dir}/fake-output",
    "IdlePollSeconds": 5,
    "MaxBackoffSeconds": 60,
    "MaxArtifactBytes": 33554432,
    "MaxTemporaryBytes": 134217728
  }
}
EOF
chown root:"$user" "$config_path"
chmod 0640 "$config_path"

read -r -s -p "Enrollment token for $instance: " token
printf '\n'
[[ -n "$token" ]] || { echo 'Enrollment token is required.' >&2; exit 2; }

cd "$install_dir"
if ! printf '%s' "$token" | runuser -u "$user" -- env DOTNET_ENVIRONMENT=Production \
  "$agent" enroll --server "${server%/}" --station "$station" --token-stdin \
  --config "$config_path"; then
  unset token
  echo 'Enrollment failed; service was not enabled.' >&2
  exit 1
fi
unset token

install -m 0644 "$script_dir/nubarca-print-agent@.service" /etc/systemd/system/nubarca-print-agent@.service
systemctl daemon-reload
systemctl enable --now "$unit"
echo "Installed $unit"
echo "Simulator output: $state_dir/fake-output"

#!/usr/bin/env bash
# Installs one SIMULATED DNP DS-RX1HS on a Linux host that has no printer — the
# server, a test machine. It is the real Print Agent on the real CUPS path: two
# queues made with the Gutenprint DS-RX1 driver, photos and strips with the
# 2-inch cut, ending in the nubarca-sim backend, which writes each job to a file
# and reports 100 prints left. In NubArca it is a DS-RX1HS that is always
# simulated.
#
# Unlike install-print-box.sh it touches neither the network nor the host name.
# Run from /opt/nubarca-print-agent/linux as root. The enrollment token is read
# silently and never reaches shell history or the process table.
set -euo pipefail

usage() {
  cat <<'USAGE'
Usage: sudo bash install-simulated-printer.sh --instance <name> --server <https-origin> --station <guid>
         [--skip-packages]

Creates the systemd instance nubarca-print-agent@<name> with the CUPS queues
NubArca-<name>-SIM and NubArca-<name>-STRIP-SIM. Installs cups, cups-ipp-utils
and printer-driver-gutenprint unless --skip-packages.
USAGE
}

instance=''
server=''
station=''
skip_packages=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --instance) instance="${2:-}"; shift 2 ;;
    --server) server="${2:-}"; shift 2 ;;
    --station) station="${2:-}"; shift 2 ;;
    --skip-packages) skip_packages=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo 'Run as root via sudo.' >&2; exit 1; }
[[ "$instance" =~ ^[a-z0-9][a-z0-9-]{0,15}$ ]] || { echo 'Instance must be 1–16 lowercase letters, digits or hyphens.' >&2; exit 2; }
[[ "$instance" != box ]] || { echo 'The instance "box" is the Print Box: choose another name.' >&2; exit 2; }
[[ "$server" =~ ^https://[^[:space:]\"]+$ ]] || { echo 'Server must be an https origin without spaces.' >&2; exit 2; }
[[ "$station" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || { echo 'Station must be a GUID.' >&2; exit 2; }

script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
install_dir="$(dirname "$script_dir")"
[[ "$install_dir" == '/opt/nubarca-print-agent' ]] || { echo 'Extract the Linux bundle exactly to /opt/nubarca-print-agent.' >&2; exit 1; }
agent="$install_dir/NubArca.PrintAgent"
chmod 0755 "$agent"

user="nubarca-print-$instance"
state_dir="/var/lib/nubarca-print-agent/$instance"
config_dir='/etc/nubarca-print-agent'
config_path="$config_dir/$instance.json"
unit="nubarca-print-agent@$instance.service"
dropin_dir="/etc/systemd/system/$unit.d"
photo="NubArca-$instance-SIM"
strip="NubArca-$instance-STRIP-SIM"
spool='/var/spool/nubarca-sim'

if systemctl list-unit-files --no-legend "$unit" | grep -q .; then
  echo "Service $unit already exists. Uninstall it before reinstalling." >&2
  exit 1
fi

# --- 1. CUPS with the DNP driver, and the simulation backend ------------------------
if [[ "$skip_packages" == false ]]; then
  apt-get update
  DEBIAN_FRONTEND=noninteractive apt-get install -y cups cups-ipp-utils printer-driver-gutenprint
fi
systemctl enable --now cups
install -m 0755 -o root -g root "$script_dir/nubarca-sim" /usr/lib/cups/backend/nubarca-sim

# --- 2. Account, state, spool --------------------------------------------------------
if ! id "$user" >/dev/null 2>&1; then
  useradd --system --user-group --home-dir "$state_dir" --shell /usr/sbin/nologin "$user"
fi
install -d -o "$user" -g "$user" -m 0700 "$state_dir" "$state_dir/temp"
install -d -o root -g root -m 0755 "$config_dir"
# Written by the backend (lp), cleared by the agent on the owner's order. One
# spool for every simulated instance on this host: each queue has its own folder.
install -d -o lp -g "$user" -m 2770 "$spool"

# --- 3. The two simulated queues -------------------------------------------------------
set +e
"$agent" setup-cups --printer "$photo" --strip-printer "$strip" --simulation-only
status=$?
set -e
case "$status" in
  0) ;;
  10) echo 'Strips will print as one sheet: this Gutenprint has no 2x6 cut size.' >&2 ;;
  *) echo 'The simulated queues could not be created; see the message above.' >&2; exit 1 ;;
esac

# --- 4. Configuration -------------------------------------------------------------------
umask 0077
cat > "$config_path.tmp" <<CONFIG
{
  "PrintAgent": {
    "ServerOrigin": "${server%/}",
    "CredentialPath": "${state_dir}/credential.bin",
    "JournalPath": "${state_dir}/journal.db",
    "TemporaryPath": "${state_dir}/temp",
    "Adapter": "cups",
    "PrinterName": "${photo}",
    "StripPrinterName": "${strip}",
    "IdlePollSeconds": 5,
    "MaxBackoffSeconds": 60,
    "MaxArtifactBytes": 33554432,
    "MaxTemporaryBytes": 134217728,
    "Simulation": {
      "QueueSuffix": "-SIM",
      "OutputPath": "${spool}",
      "Only": true
    },
    "NetworkProvisioning": { "Enabled": false }
  }
}
CONFIG
chown root:"$user" "$config_path.tmp"
chmod 0640 "$config_path.tmp"
mv -f "$config_path.tmp" "$config_path"

# --- 5. Enrollment ----------------------------------------------------------------------
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

# --- 6. Service -------------------------------------------------------------------------
install -m 0644 "$script_dir/nubarca-print-agent@.service" /etc/systemd/system/nubarca-print-agent@.service
install -d -m 0755 "$dropin_dir"
cat > "$dropin_dir/simulation.conf" <<'DROPIN'
[Unit]
Wants=cups.service
After=cups.service

[Service]
ReadWritePaths=/var/spool/nubarca-sim
DROPIN
systemctl daemon-reload
systemctl enable --now "$unit"
echo "Installed $unit: a simulated DS-RX1HS on $photo and $strip."
echo "Its prints are shown, downloaded and cleared on the printers page in NubArca."

#!/usr/bin/env bash
# Prepares a minimal Debian machine as a headless NubArca Print Box: CUPS with
# Gutenprint for the DNP DS-RX1/RX1HS, NetworkManager for Wi-Fi, and the Print
# Agent as the systemd instance nubarca-print-agent@box.
#
# Idempotent: run it again to change the queues or the setup password. The
# station is enrolled once; --reenroll replaces its credential on purpose.
#
# Run from /opt/nubarca-print-agent/linux as root. The enrollment token and the
# setup-network password are read silently from the terminal — never taken as
# arguments, so they never reach shell history or the process table.
set -euo pipefail
# One locale for every check below: "printable" means printable ASCII, exactly
# what the agent accepts for the setup password.
export LC_ALL=C

usage() {
  cat <<'EOF'
Usage: sudo ./install-print-box.sh --server <https-origin> --station <guid> --printer <cups-queue>
         [--strip-printer <cups-queue>] [--hostname <name>] [--open-setup-network]
         [--no-network-provisioning] [--skip-packages] [--reenroll]

  --printer             CUPS queue for photos (10x15), 2-inch cut OFF.
  --strip-printer       CUPS queue on the SAME printer with the 2-inch cut ON;
                        only then does the box offer strips cut by the printer.
  --hostname            Set the machine name, e.g. nubarca-print, so the setup
                        page is also reachable as http://<name>.local:8080.
  --open-setup-network  Leave the setup Wi-Fi without a password (first tests
                        only). Otherwise a password is asked for.
  --no-network-provisioning
                        Wired or already-configured networks only: no setup
                        Wi-Fi, no setup page.
  --skip-packages       Do not run apt-get (packages already installed).
  --reenroll            Enroll again even if this box already has a credential.

The CUPS queues themselves are created by the operator (see docs/print-agent.md,
"Linux Print Box"); this script checks that they exist.
EOF
}

server=''
station=''
printer=''
strip=''
host_name=''
open_network=false
provisioning=true
skip_packages=false
reenroll=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --server) server="${2:-}"; shift 2 ;;
    --station) station="${2:-}"; shift 2 ;;
    --printer) printer="${2:-}"; shift 2 ;;
    --strip-printer) strip="${2:-}"; shift 2 ;;
    --hostname) host_name="${2:-}"; shift 2 ;;
    --open-setup-network) open_network=true; shift ;;
    --no-network-provisioning) provisioning=false; shift ;;
    --skip-packages) skip_packages=true; shift ;;
    --reenroll) reenroll=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

queue_re='^[A-Za-z0-9_.-]{1,127}$'
[[ $EUID -eq 0 ]] || { echo 'Run as root via sudo.' >&2; exit 1; }
[[ "$server" =~ ^https://[^[:space:]\"\\]+$ ]] || { echo 'Server must be an https origin without spaces or quotes.' >&2; exit 2; }
[[ "$station" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]] || { echo 'Station must be a GUID.' >&2; exit 2; }
[[ "$printer" =~ $queue_re ]] || { echo 'Printer must be a CUPS queue name: letters, digits, dot, dash, underscore.' >&2; exit 2; }
if [[ -n "$strip" ]]; then
  [[ "$strip" =~ $queue_re ]] || { echo 'Strip printer must be a CUPS queue name: letters, digits, dot, dash, underscore.' >&2; exit 2; }
  [[ "${strip,,}" != "${printer,,}" ]] || { echo 'The strip queue must be a second queue, not the photo queue itself.' >&2; exit 2; }
fi
if [[ -n "$host_name" ]]; then
  [[ "$host_name" =~ ^[a-z0-9][a-z0-9-]{0,62}$ ]] || { echo 'Hostname must be lowercase letters, digits and hyphens.' >&2; exit 2; }
fi

script_dir="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"
install_dir="$(dirname "$script_dir")"
[[ "$install_dir" == '/opt/nubarca-print-agent' ]] || { echo 'Extract the Linux bundle exactly to /opt/nubarca-print-agent.' >&2; exit 1; }
agent="$install_dir/NubArca.PrintAgent"
[[ -f "$agent" ]] || { echo "Missing executable: $agent" >&2; exit 1; }
# A GitHub artifact zip does not keep execute bits.
chmod 0755 "$agent"

instance='box'
user="nubarca-print-$instance"
state_dir="/var/lib/nubarca-print-agent/$instance"
config_dir='/etc/nubarca-print-agent'
config_path="$config_dir/$instance.json"
unit="nubarca-print-agent@$instance.service"
dropin_dir="/etc/systemd/system/$unit.d"
polkit_rule='/etc/polkit-1/rules.d/49-nubarca-print-box.rules'

# --- 1. Packages ---------------------------------------------------------------
if [[ "$skip_packages" == false ]]; then
  export DEBIAN_FRONTEND=noninteractive
  apt-get update
  # The self-contained agent still needs the system ICU library.
  icu="$(apt-cache pkgnames libicu | grep -E '^libicu[0-9]+$' | sort -V | tail -n 1 || true)"
  [[ -n "$icu" ]] || { echo 'No libicu package found in the configured APT sources.' >&2; exit 1; }
  packages=(ca-certificates "$icu" cups printer-driver-gutenprint avahi-daemon)
  if [[ "$provisioning" == true ]]; then
    # dnsmasq-base serves addresses on the setup network; polkitd reads the
    # JavaScript rule below (Debian 12 and later).
    packages+=(network-manager dnsmasq-base polkitd)
  fi
  apt-get install -y --no-install-recommends "${packages[@]}"
fi

systemctl enable --now cups
systemctl enable --now avahi-daemon || echo 'avahi-daemon did not start; .local names will not work.' >&2
if [[ "$provisioning" == true ]]; then
  systemctl enable --now NetworkManager
  # NetworkManager leaves alone any interface that /etc/network/interfaces
  # configures. Taking an interface away from ifupdown over the very connection
  # this script runs on would cut it, so the script says so instead of doing it.
  ifupdown=()
  for file in /etc/network/interfaces /etc/network/interfaces.d/*; do
    [[ -f "$file" ]] && ifupdown+=("$file")
  done
  if (( ${#ifupdown[@]} > 0 )) \
     && awk '$1 == "iface" && $2 != "lo" { found = 1 } END { exit !found }' "${ifupdown[@]}"; then
    echo 'WARNING: /etc/network/interfaces configures a network interface. NetworkManager will not manage it,' >&2
    echo '         so the setup Wi-Fi cannot use it. Move that configuration to NetworkManager (see docs/print-agent.md).' >&2
  fi
fi

if [[ -n "$host_name" ]]; then
  hostnamectl set-hostname "$host_name"
fi

# --- 2. Account, state and permissions -----------------------------------------
if ! id "$user" >/dev/null 2>&1; then
  useradd --system --user-group --home-dir "$state_dir" --shell /usr/sbin/nologin "$user"
fi
install -d -o "$user" -g "$user" -m 0700 "$state_dir" "$state_dir/temp"
install -d -o root -g root -m 0755 "$config_dir"

if [[ "$provisioning" == true ]]; then
  install -d -m 0755 /etc/polkit-1/rules.d
  install -m 0644 "$script_dir/nubarca-print-box.rules" "$polkit_rule"
else
  rm -f "$polkit_rule"
fi

# --- 3. Configuration -------------------------------------------------------------
setup_password=''
if [[ "$provisioning" == true && "$open_network" == false ]]; then
  while true; do
    read -r -s -p 'Password for the setup Wi-Fi (8-63 characters): ' setup_password
    printf '\n'
    read -r -s -p 'Repeat it: ' repeat
    printf '\n'
    if [[ "$setup_password" != "$repeat" ]]; then echo 'They differ; try again.' >&2; continue; fi
    if [[ ${#setup_password} -lt 8 || ${#setup_password} -gt 63 || ! "$setup_password" =~ ^[\ -~]+$ ]]; then
      echo 'Use 8 to 63 printable characters.' >&2; continue
    fi
    break
  done
  unset repeat
fi
json_escape() { local v="${1//\\/\\\\}"; printf '%s' "${v//\"/\\\"}"; }
strip_json='null'
[[ -n "$strip" ]] && strip_json="\"$strip\""

umask 0077
cat > "$config_path.tmp" <<EOF
{
  "PrintAgent": {
    "ServerOrigin": "${server%/}",
    "CredentialPath": "${state_dir}/credential.bin",
    "JournalPath": "${state_dir}/journal.db",
    "TemporaryPath": "${state_dir}/temp",
    "Adapter": "cups",
    "PrinterName": "${printer}",
    "StripPrinterName": ${strip_json},
    "IdlePollSeconds": 5,
    "MaxBackoffSeconds": 60,
    "MaxArtifactBytes": 33554432,
    "MaxTemporaryBytes": 134217728,
    "NetworkProvisioning": {
      "Enabled": ${provisioning},
      "ConnectionGraceSeconds": 30,
      "AccessPointPrefix": "NubArca-Print",
      "AccessPointPassword": "$(json_escape "$setup_password")",
      "WebPort": 8080
    }
  }
}
EOF
unset setup_password
chown root:"$user" "$config_path.tmp"
chmod 0640 "$config_path.tmp"
mv -f "$config_path.tmp" "$config_path"

# --- 4. Enrollment -------------------------------------------------------------------
if [[ "$reenroll" == true || ! -s "$state_dir/credential.bin" ]]; then
  read -r -s -p 'Enrollment token for this Print Box: ' token
  printf '\n'
  [[ -n "$token" ]] || { echo 'Enrollment token is required.' >&2; exit 2; }
  cd "$install_dir"
  if ! printf '%s' "$token" | runuser -u "$user" -- env DOTNET_ENVIRONMENT=Production \
    "$agent" enroll --server "${server%/}" --station "$station" --token-stdin \
    --config "$config_path"; then
    unset token
    echo 'Enrollment failed; the service was not (re)started.' >&2
    exit 1
  fi
  unset token
else
  echo 'Already enrolled; keeping the existing credential (use --reenroll to replace it).'
fi

# --- 5. Service ------------------------------------------------------------------------
install -m 0644 "$script_dir/nubarca-print-agent@.service" /etc/systemd/system/nubarca-print-agent@.service
install -d -m 0755 "$dropin_dir"
cat > "$dropin_dir/print-box.conf" <<'EOF'
[Unit]
Description=NubArca Print Box
Wants=cups.service NetworkManager.service
After=cups.service NetworkManager.service
EOF
systemctl daemon-reload
systemctl enable "$unit"
systemctl restart "$unit"

# --- 6. What is left to do ---------------------------------------------------------------
missing=0
for queue in "$printer" ${strip:+"$strip"}; do
  if ! lpstat -p "$queue" >/dev/null 2>&1; then
    echo "WARNING: CUPS queue '$queue' does not exist yet. Create it (docs/print-agent.md, \"Linux Print Box\")." >&2
    missing=1
  fi
done
echo "Installed $unit."
if [[ "$provisioning" == true ]]; then
  echo 'Without a known network the box opens the setup Wi-Fi about 30 seconds after boot. Its name:'
  echo "  journalctl -u $unit | grep 'Setup network'"
  echo 'Setup page: http://<the setup network gateway>:8080 (NetworkManager usually hands out 10.42.0.x),'
  [[ -n "$host_name" ]] && echo "            or http://$host_name.local:8080 where mDNS works."
  if [[ "$open_network" == true ]]; then
    echo 'WARNING: the setup Wi-Fi is OPEN. Re-run without --open-setup-network before installing the box anywhere.' >&2
  fi
fi
exit "$missing"

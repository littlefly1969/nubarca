#!/usr/bin/env bash
# Prepares Ubuntu Server (24.04 LTS or later) or Debian (12 or later) as a
# headless NubArca Print Box: CUPS with Gutenprint for the DNP DS-RX1/RX1HS,
# its two print queues, NetworkManager for Wi-Fi, and the Print Agent as the
# systemd instance nubarca-print-agent@box. Nothing is left to do by hand.
#
# Idempotent: run it again after plugging the printer in, or to change the
# setup password. The station is enrolled once; --reenroll replaces its
# credential on purpose.
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
Usage: sudo bash install-print-box.sh --server <https-origin> --station <guid>
         [--printer <queue>] [--strip-printer <queue>] [--hostname <name> | --keep-hostname]
         [--open-setup-network] [--no-network-provisioning] [--no-cups-setup]
         [--skip-packages] [--reenroll]

  --printer             CUPS queue for photos, 4x6 (default NubArca-RX1HS).
  --strip-printer       CUPS queue for strips, 4x6 cut into 2x6 x2
                        (default NubArca-RX1HS-STRIP).
  --hostname            Machine name (default nubarca-print): the setup page is
                        then also http://<name>.local:8080. --keep-hostname
                        leaves the current one.
  --open-setup-network  Leave the setup Wi-Fi without a password (first tests
                        only). Otherwise a password is asked for.
  --no-network-provisioning
                        Wired or already-configured networks only: no setup
                        Wi-Fi, no setup page, network configuration untouched.
  --no-cups-setup       The two queues exist already and must not be touched.
  --skip-packages       Do not run apt-get (packages already installed).
  --reenroll            Enroll again even if this box already has a credential.

With the DNP plugged in and switched on, the two queues are created
automatically. On Ubuntu Server the network is handed from networkd to
NetworkManager at the very end; an SSH session may pause for a few seconds.
EOF
}

server=''
station=''
printer='NubArca-RX1HS'
strip='NubArca-RX1HS-STRIP'
host_name='nubarca-print'
open_network=false
cups_setup=true
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
    --keep-hostname) host_name=''; shift ;;
    --no-cups-setup) cups_setup=false; shift ;;
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
[[ "$strip" =~ $queue_re ]] || { echo 'Strip printer must be a CUPS queue name: letters, digits, dot, dash, underscore.' >&2; exit 2; }
[[ "${strip,,}" != "${printer,,}" ]] || { echo 'The strip queue must be a second queue, not the photo queue itself.' >&2; exit 2; }

# polkit JavaScript rules and a current NetworkManager: Ubuntu 24.04, Debian 12.
# shellcheck disable=SC1091
. /etc/os-release
version_ok() { [[ "$(printf '%s\n%s\n' "$2" "$1" | sort -V | head -n 1)" == "$2" ]]; }
case "${ID:-}" in
  ubuntu) version_ok "${VERSION_ID:-0}" 24.04 || { echo 'Use Ubuntu Server 24.04 LTS or later.' >&2; exit 1; } ;;
  debian) version_ok "${VERSION_ID:-0}" 12 || { echo 'Use Debian 12 or later.' >&2; exit 1; } ;;
  *) echo "Unsupported system '${ID:-unknown}': use Ubuntu Server 24.04 LTS or later, or Debian 12 or later." >&2; exit 1 ;;
esac
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
  # cups-ipp-utils carries ipptool, which reads the printer's remaining-prints
  # marker from CUPS (linux/nubarca-media-status.test).
  packages=(ca-certificates "$icu" cups cups-ipp-utils printer-driver-gutenprint avahi-daemon iw)
  if [[ "$provisioning" == true ]]; then
    # dnsmasq-base serves addresses on the setup network; polkitd reads the
    # JavaScript rule below (Debian 12 and later); nftables carries nft, which
    # sends plain http://<gateway>/ on the setup network to the setup page.
    packages+=(network-manager dnsmasq-base polkitd nftables)
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

if [[ -n "$host_name" && "$(hostname)" != "$host_name" ]]; then
  hostnamectl set-hostname "$host_name"
  if grep -q '^127\.0\.1\.1' /etc/hosts; then
    sed -i "s/^127\.0\.1\.1.*/127.0.1.1\t$host_name/" /etc/hosts
  else
    printf '127.0.1.1\t%s\n' "$host_name" >> /etc/hosts
  fi
fi

# Ubuntu Server hands its interfaces to systemd-networkd through netplan, and
# NetworkManager leaves those alone — so the setup Wi-Fi could never open.
# One more netplan file makes NetworkManager the renderer for every interface,
# keeping each one's existing settings (DHCP or static). It is applied at the
# very end, when nothing else here still needs the network.
switch_netplan=false
if [[ "$provisioning" == true ]] && command -v netplan >/dev/null 2>&1; then
  if [[ "$(netplan get renderer 2>/dev/null || true)" != 'NetworkManager' ]]; then
    (umask 0077; printf 'network:\n  version: 2\n  renderer: NetworkManager\n' \
      > /etc/netplan/99-nubarca-network-manager.yaml)
    netplan generate
    # networkd no longer owns a link; its wait-online would only delay every boot.
    systemctl disable systemd-networkd-wait-online.service 2>/dev/null || true
    switch_netplan=true
  fi
fi

# --- 1b. The printer's two queues ------------------------------------------------
if [[ "$cups_setup" == true ]]; then
  set +e
  "$agent" setup-cups --printer "$printer" --strip-printer "$strip"
  cups_status=$?
  set -e
  case "$cups_status" in
    0) ;;
    10) echo 'Strips will print as one sheet: this Gutenprint has no 2x6 cut size.' >&2 ;;
    3) echo 'The DNP was not found. Plug it in, switch it on and run this same command again.' >&2 ;;
    *) echo 'The print queues could not be created; see the message above.' >&2 ;;
  esac
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
# The strip queue is always named: the agent offers 2x6x2 only while CUPS
# actually has it, so a missing one costs nothing.
strip_json="\"$strip\""

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
if [[ "$provisioning" == true ]]; then
  # While the setup network is up, the agent redirects port 80 on it to the
  # setup page (nft, one table of its own, removed when the setup network
  # closes). nft needs CAP_NET_ADMIN: this instance only, as an ambient
  # capability — not root, no sudo, never the simulators' instances.
  cat > "$dropin_dir/captive-portal.conf" <<'EOF'
[Service]
AmbientCapabilities=CAP_NET_ADMIN
EOF
else
  rm -f "$dropin_dir/captive-portal.conf"
fi
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
  echo 'Setup page: http://<the setup network gateway>/ (NetworkManager usually hands out 10.42.0.x),'
  echo '            or http://<gateway>:8080/, and over Ethernet http://<the box address>:8080/'
  [[ -n "$host_name" ]] && echo "            or http://$host_name.local:8080 where mDNS works."
  if [[ "$open_network" == true ]]; then
    echo 'WARNING: the setup Wi-Fi is OPEN. Re-run without --open-setup-network before installing the box anywhere.' >&2
  fi
fi
if [[ "$switch_netplan" == true ]]; then
  echo 'The network moves to NetworkManager in 10 seconds. An SSH session may pause or drop;'
  echo 'reconnect to the same address. The installation itself is complete.'
  systemd-run --quiet --collect --on-active=10 --unit=nubarca-netplan-apply "$(command -v netplan)" apply
fi
exit "$missing"

#!/usr/bin/env bash
# Removes a Linux Print Agent service. State is retained unless --purge-state.
set -euo pipefail

usage() {
  echo 'Usage: sudo ./uninstall-instance.sh --instance <name> [--purge-state]'
}

instance=''
purge=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --instance) instance="${2:-}"; shift 2 ;;
    --purge-state) purge=true; shift ;;
    --help|-h) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

[[ $EUID -eq 0 ]] || { echo 'Run as root via sudo.' >&2; exit 1; }
[[ "$instance" =~ ^[a-z0-9][a-z0-9-]{0,15}$ ]] || { echo 'Instance must be 1–16 lowercase letters, digits or hyphens.' >&2; exit 2; }
unit="nubarca-print-agent@$instance.service"
user="nubarca-print-$instance"
state_dir="/var/lib/nubarca-print-agent/$instance"

systemctl disable --now "$unit" 2>/dev/null || true
rm -f "/etc/nubarca-print-agent/$instance.json"
# What install-print-box.sh adds for the Print Box instance.
rm -rf "/etc/systemd/system/$unit.d"
if [[ "$instance" == box ]]; then
  rm -f /etc/polkit-1/rules.d/49-nubarca-print-box.rules
  systemctl disable --now nubarca-print-agent-update.path 2>/dev/null || true
  rm -f /etc/systemd/system/nubarca-print-agent-update.path /etc/systemd/system/nubarca-print-agent-update.service \
    /etc/nubarca-print-agent/self-update
fi
# The simulated queues install-simulated-printer.sh made for this instance.
if command -v lpadmin >/dev/null 2>&1; then
  for queue in "NubArca-$instance-SIM" "NubArca-$instance-STRIP-SIM"; do
    lpadmin -x "$queue" 2>/dev/null || true
    [[ "$purge" == true ]] && rm -rf "/var/spool/nubarca-sim/$queue"
  done
fi
if [[ "$purge" == true ]]; then
  userdel "$user" 2>/dev/null || true
  rm -rf "$state_dir"
else
  echo "Retained $state_dir (credential and journal). Use --purge-state only when intentionally replacing the station."
fi
systemctl daemon-reload

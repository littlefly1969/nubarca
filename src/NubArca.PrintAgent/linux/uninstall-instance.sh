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
if [[ "$purge" == true ]]; then
  userdel "$user" 2>/dev/null || true
  rm -rf "$state_dir"
else
  echo "Retained $state_dir (credential and journal). Use --purge-state only when intentionally replacing the station."
fi
systemctl daemon-reload

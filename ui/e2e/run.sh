#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
keyring_home="$(mktemp -d)"
trap 'rm -rf "$keyring_home"' EXIT

dbus-run-session --config-file=session-bus.conf -- bash -c '
  export SCOUT_E2E_PRIVATE_SESSION=1
  mkdir -m 700 -p "$1/run"
  printf scout-e2e | HOME="$1" XDG_DATA_HOME="$1/share" XDG_RUNTIME_DIR="$1/run" gnome-keyring-daemon --unlock --components=secrets >/dev/null
  shift
  xvfb-run -a -s "-screen 0 1920x1080x24" playwright test "$@"
' _ "$keyring_home" "$@"

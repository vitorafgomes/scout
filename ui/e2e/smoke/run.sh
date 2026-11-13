#!/usr/bin/env bash
set -euo pipefail

cd "$(dirname "$0")"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
trap 'exit 143' INT TERM

if [ -n "${SCOUT_SMOKE_APP_DIR:-}" ]; then
  app="$(realpath "$SCOUT_SMOKE_APP_DIR")"
else
  shopt -s nullglob
  images=(../../build/dist/Scout-Desktop-*-linux-*.AppImage)
  if [ "${#images[@]}" -ne 1 ]; then
    echo "Expected one AppImage in ui/build/dist, found ${#images[@]}; run bun run dist:linux first." >&2
    exit 1
  fi
  image="$(realpath "${images[0]}")"
  (cd "$work" && "$image" --appimage-extract >/dev/null)
  app="$work/squashfs-root"
fi

mkdir -m 700 "$work/run"
export SCOUT_SMOKE_APP="$app" SCOUT_SMOKE_WORK="$work" XDG_RUNTIME_DIR="$work/run"
unshare --user --map-root-user --net --pid --fork --mount-proc -- bash -c '
  ip link set lo up
  exec unshare --user --map-user="$1" --map-group="$2" -- bash ../run.sh -c smoke/playwright.config.ts "${@:3}"
' _ "$(id -u)" "$(id -g)" "$@"

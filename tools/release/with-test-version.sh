#!/usr/bin/env bash
# Usage: tools/release/with-test-version.sh <command...>
#
# Sets the version in ui/package.json to `next-version.sh test`, runs the command in ui/, and puts package.json
# back byte for byte however the command ends: success, failure, Ctrl+C or a kill.
set -euo pipefail
[ $# -gt 0 ] || { echo "Usage: with-test-version.sh <command...>" >&2; exit 2; }
root=$(cd "$(dirname "$0")/../.." && pwd)
pkg="$root/ui/package.json"
version=$(cd "$root" && bash tools/release/next-version.sh test)
backup=$(mktemp)
cp "$pkg" "$backup"
trap 'cp "$backup" "$pkg"; rm -f "$backup"' EXIT
cd "$root/ui"
npm pkg set version="$version"
echo "Building Scout Desktop $version"
"$@"

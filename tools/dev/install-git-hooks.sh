#!/bin/sh
# Copies the versioned graphify hooks into the clone's common hooks dir, shared by every worktree. Idempotent.
set -eu
src="$(cd "$(dirname "$0")" && pwd)/git-hooks"
hooks="$(git rev-parse --path-format=absolute --git-common-dir)/hooks"
mkdir -p "$hooks"
for name in post-commit post-checkout post-merge; do
    target="$hooks/$name"
    stamp="$hooks/$name.scout"
    if [ -f "$target" ] && cmp -s "$src/$name" "$target"; then
        chmod +x "$target"
        cp "$src/$name" "$stamp"
        echo "unchanged  $target"
        continue
    fi
    if [ -f "$target" ] && [ -f "$stamp" ] && cmp -s "$stamp" "$target"; then
        echo "updated    $target"
    elif [ -f "$target" ]; then
        bak="$target.bak"; n=1
        while [ -e "$bak" ]; do n=$((n + 1)); bak="$target.bak.$n"; done
        cp "$target" "$bak"
        echo "replaced   $target (previous kept as $(basename "$bak"))"
    else
        echo "installed  $target"
    fi
    cp "$src/$name" "$target"
    chmod +x "$target"
    cp "$src/$name" "$stamp"
done

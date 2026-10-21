#!/bin/sh
# Copies the versioned graphify hooks into the clone's common hooks dir, shared by every worktree. Idempotent.
set -eu
src="$(cd "$(dirname "$0")" && pwd)/git-hooks"
hooks="$(git rev-parse --path-format=absolute --git-common-dir)/hooks"
mkdir -p "$hooks"
for name in post-commit post-checkout post-merge; do
    target="$hooks/$name"
    if [ -f "$target" ] && cmp -s "$src/$name" "$target"; then
        chmod +x "$target"
        echo "unchanged  $target"
        continue
    fi
    if [ -f "$target" ]; then
        cp "$target" "$target.bak"
        echo "replaced   $target (previous kept as $name.bak)"
    else
        echo "installed  $target"
    fi
    cp "$src/$name" "$target"
    chmod +x "$target"
done

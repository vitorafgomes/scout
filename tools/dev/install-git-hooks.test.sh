#!/usr/bin/env bash
# Case matrix for install-git-hooks.sh, run in a throwaway git repo so the clone's own hooks are never touched.
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
unset GIT_DIR GIT_WORK_TREE GIT_COMMON_DIR GIT_INDEX_FILE
fail=0
setup() { rm -rf "$work/repo"; mkdir -p "$work/repo/tools/dev"
  git -C "$work/repo" init -q
  cp "$SCRIPT_DIR/install-git-hooks.sh" "$work/repo/tools/dev/"; cp -r "$SCRIPT_DIR/git-hooks" "$work/repo/tools/dev/"
  hooks="$work/repo/.git/hooks"; }
install() { out=$(cd "$work/repo" && sh tools/dev/install-git-hooks.sh 2>&1); rc=$?
  [ "$rc" = 0 ] || { echo "FAIL install exited $rc :: $1"; echo "$out" | sed 's/^/    /'; fail=1; }; }
same() { if ! cmp -s "$1" "$2"; then echo "FAIL $3"; fail=1; fi; }

setup; printf '#!/bin/sh\necho my own hook\n' > "$hooks/post-commit"; cp "$hooks/post-commit" "$work/custom"
install "over a custom hook"
same "$hooks/post-commit.bak" "$work/custom" "the first install did not back up the custom hook"
printf '\n# a later version\n' >> "$work/repo/tools/dev/git-hooks/post-commit"
install "after the versioned hook changed"
same "$hooks/post-commit" "$work/repo/tools/dev/git-hooks/post-commit" "the second install did not install the new versioned hook"
same "$hooks/post-commit.bak" "$work/custom" "a later install overwrote the backup of the user's original hook"

setup; install "into an empty hooks directory"
for name in post-commit post-checkout post-merge; do
  same "$hooks/$name" "$work/repo/tools/dev/git-hooks/$name" "a fresh install did not install $name"
  [ -x "$hooks/$name" ] || { echo "FAIL a fresh install left $name not executable"; fail=1; }
  [ -e "$hooks/$name.bak" ] && { echo "FAIL a fresh install made a backup of $name with nothing to back up"; fail=1; }
done
install "unchanged hooks"
printf '%s' "$out" | grep -q "unchanged  $hooks/post-commit" || { echo "FAIL an unchanged hook was not reported unchanged: $out"; fail=1; }

setup; install "fresh, before the versioned hook changes"
printf '\n# a later version\n' >> "$work/repo/tools/dev/git-hooks/post-commit"
install "over the hook this script installed"
[ -e "$hooks/post-commit.bak" ] && { echo "FAIL the script backed up a hook it had installed itself"; fail=1; }
printf '#!/bin/sh\necho written after Scout\n' > "$hooks/post-commit"; cp "$hooks/post-commit" "$work/custom"
install "over a hook the owner wrote after Scout's"
same "$hooks/post-commit.bak" "$work/custom" "a hook the owner wrote after Scout's own was overwritten without a backup"
printf '#!/bin/sh\necho a second hook of my own\n' > "$hooks/post-commit"; cp "$hooks/post-commit" "$work/second"
install "over a second hook of the owner's"
same "$hooks/post-commit.bak" "$work/custom" "a second hook of the owner's overwrote the first backup"
same "$hooks/post-commit.bak.2" "$work/second" "a second hook of the owner's was not kept beside the first backup"

[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

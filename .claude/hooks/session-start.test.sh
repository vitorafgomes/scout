#!/usr/bin/env bash
# Case matrix for session-start.sh: WARN = must emit the named dead-hooks message, QUIET = must emit nothing; then the code graph cases.
HOOK="${1:-$(dirname "$0")/session-start.sh}"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
fail=0
check() {
  want=$1; name=$2; expect=$3; shift 3
  got=$(printf '{}' | env CLAUDE_PROJECT_DIR="$work" "$@" sh "$HOOK" | grep -c "$expect")
  [ "$got" = "1" ] && actual=WARN || actual=QUIET
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $name"; fail=1; fi
}
# (a) the real guard, jq present
check QUIET "real guard answers" systemMessage PATH="$PATH"
# (b) a guard that never answers
printf '#!/bin/sh\nexit 0\n' > "$work/mute.sh"
check WARN "mute guard" "did not answer" PRE_GIT_GUARD="$work/mute.sh"
# (c) jq missing: PATH holds only a directory with sh and no jq
mkdir -p "$work/bin"; ln -s "$(command -v sh)" "$work/bin/sh"; ln -s "$(command -v dirname)" "$work/bin/dirname"
check WARN "jq missing" "jq is not on PATH" PATH="$work/bin"
# The code graph: every case runs in a throwaway clone with a fake graphify, so the real clone and the real graph are never touched.
SRC="$(cd "$(dirname "$0")/../.." && pwd)"
mkdir -p "$work/fake"
printf '#!/bin/sh\necho "$PWD :: $*" >> "$GRAPHIFY_CALLS"\n[ -n "${GRAPHIFY_FAIL:-}" ] && exit 1\nmkdir -p graphify-out && : > graphify-out/graph.json\n' > "$work/fake/graphify"
chmod +x "$work/fake/graphify"
n=0
clone() {
  n=$((n + 1)); repo="$work/repo$n"; calls="$work/calls$n"; : > "$calls"
  git init -q "$repo"
  mkdir -p "$repo/tools/dev"; cp "$SRC/tools/dev/install-git-hooks.sh" "$repo/tools/dev/"; cp -r "$SRC/tools/dev/git-hooks" "$repo/tools/dev/"
  git -C "$repo" add -A; git -C "$repo" -c user.name=t -c user.email=t@t commit -q -m init
  head=$(git -C "$repo" rev-parse HEAD)
}
run() { out=$(printf '{}' | env CLAUDE_PROJECT_DIR="$repo" GRAPHIFY_CALLS="$calls" PATH="$work/fake:$PATH" "$@" sh "$HOOK"); rc=$?; sleep 1; }
expect() { if ! eval "$2"; then echo "FAIL :: $1"; fail=1; fi; }
# (d) a fresh clone: the three hooks are installed, the graph is built from the clone's root, HEAD is recorded, nothing is printed
clone; run
expect "fresh clone installs the hooks" '[ -x "$repo/.git/hooks/post-commit" ] && [ -x "$repo/.git/hooks/post-checkout" ] && [ -x "$repo/.git/hooks/post-merge" ]'
expect "fresh clone builds the graph" 'grep -qx "$repo :: update . --no-cluster" "$calls"'
expect "fresh clone records HEAD" '[ "$(cat "$repo/graphify-out/.scout-head")" = "$head" ]'
expect "fresh clone is quiet" '[ -z "$out" ]'
# (e) the graph is at HEAD: no rebuild
: > "$calls"; run
expect "graph at HEAD is not rebuilt" '[ ! -s "$calls" ]'
# (f) HEAD moved since the graph was built: rebuild
echo stale > "$repo/graphify-out/.scout-head"; run
expect "stale graph is rebuilt" '[ "$(wc -l < "$calls")" = 1 ] && [ "$(cat "$repo/graphify-out/.scout-head")" = "$head" ]'
# (g) the rebuild fails: quiet, exit 0, and HEAD is not recorded, so the next session tries again
clone; run GRAPHIFY_FAIL=1
expect "failed rebuild is quiet and exits 0" '[ $rc = 0 ] && [ -z "$out" ] && [ -s "$calls" ]'
expect "failed rebuild records nothing" '[ ! -e "$repo/graphify-out/.scout-head" ]'
# (h) a post-commit hook the owner wrote is left alone
clone; echo mine > "$repo/.git/hooks/post-commit"; run
expect "the owner's hook is kept" '[ "$(cat "$repo/.git/hooks/post-commit")" = mine ] && [ ! -e "$repo/.git/hooks/post-merge" ]'
# (k) the clone already has the hooks this repo installed: a newer versioned hook replaces the installed one, a deleted one comes back
clone; run; printf '\n# a later version\n' >> "$repo/tools/dev/git-hooks/post-commit"; rm "$repo/.git/hooks/post-merge"; run
expect "an installed hook is brought up to date" 'cmp -s "$repo/tools/dev/git-hooks/post-commit" "$repo/.git/hooks/post-commit" && [ -x "$repo/.git/hooks/post-merge" ]'
# (i) a linked worktree: the hooks go to the common directory, the graph is built in the worktree
clone; main="$repo"; git -C "$main" worktree add -q "$work/wt" -b wt; repo="$work/wt"; run
expect "worktree installs into the common hooks" '[ -x "$main/.git/hooks/post-commit" ]'
expect "worktree builds its own graph" 'grep -qx "$work/wt :: update . --no-cluster" "$calls" && [ ! -e "$main/graphify-out" ]'
# (j) graphify is not installed and everything else the hook needs is: nothing is installed, nothing is built
mkdir -p "$work/nograph"; for tool in sh dirname git jq cat cp cmp chmod mkdir grep sed; do ln -s "$(command -v $tool)" "$work/nograph/$tool"; done
clone; out=$(printf '{}' | env CLAUDE_PROJECT_DIR="$repo" GRAPHIFY_CALLS="$calls" PATH="$work/nograph" sh "$HOOK"); sleep 1
expect "no graphify, nothing happens" '[ ! -e "$repo/.git/hooks/post-commit" ] && [ ! -s "$calls" ] && [ ! -e "$repo/graphify-out" ]'
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

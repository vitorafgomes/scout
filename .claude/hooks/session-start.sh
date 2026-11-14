#!/bin/sh
# SessionStart hook: keep the code graph current, then prove the hook runtime works, loudly.
#
# On a machine that has graphify it installs the git hooks that refresh the graph, unless the
# clone has a post-commit hook this repo did not install, and rebuilds the graph in the
# background when it is missing or was built at another commit. Without graphify it does nothing.
#
# Every other hook fails open, so a missing jq is indistinguishable from a hook with nothing to
# say. This one runs the git guard on a must-ask command and warns when the answer is not
# "ask". That check depends on nothing but sh.

cat >/dev/null 2>&1

DIR="$(dirname "$0")"
GUARD="${PRE_GIT_GUARD:-$DIR/pre-git-guard.sh}"
ROOT="${CLAUDE_PROJECT_DIR:-$DIR/../..}"

if command -v graphify >/dev/null 2>&1 && HEAD=$(git -C "$ROOT" rev-parse HEAD 2>/dev/null); then
  HOOKS="$(git -C "$ROOT" rev-parse --path-format=absolute --git-common-dir)/hooks"
  if [ ! -e "$HOOKS/post-commit" ] || [ -e "$HOOKS/post-commit.scout" ]; then
    (cd "$ROOT" && sh tools/dev/install-git-hooks.sh) >/dev/null 2>&1
  fi
  MARK="$ROOT/graphify-out/.scout-head"
  if [ ! -f "$ROOT/graphify-out/graph.json" ] || [ "$(cat "$MARK" 2>/dev/null)" != "$HEAD" ]; then
    (cd "$ROOT" && graphify update . --no-cluster && printf '%s' "$HEAD" > "$MARK") >/dev/null 2>&1 &
  fi
fi

if ! command -v jq >/dev/null 2>&1; then
  printf '%s' '{"systemMessage":"The Scout hooks are silent on this machine: jq is not on PATH, so nothing they guard is guarded. Install jq and restart."}'
  exit 0
fi

OUT=$(printf '%s' '{"tool_input":{"command":"git commit -m x"}}' | sh "$GUARD" 2>/dev/null)
case "$OUT" in
  *permissionDecision*) ;;
  *) printf '%s' '{"systemMessage":"The Scout hooks are silent on this machine: pre-git-guard.sh did not answer on a command it must ask about. Run bash .claude/hooks/pre-git-guard.test.sh and fix it before trusting any hook."}' ;;
esac

exit 0

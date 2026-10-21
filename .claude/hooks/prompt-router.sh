#!/bin/sh
# UserPromptSubmit hook: inject the pipeline router + skill-selection announcement on every prompt.
#
# Injected on EVERY prompt, so it stays short on purpose - it is a router, not a rulebook.
# The second clause exists because skill matching is otherwise silent: with 85+ installed skills,
# one that never fires is indistinguishable from one that does not exist.
#
# Fails open: any error exits 0 having written nothing, so a broken hook can never block a prompt.

cat >/dev/null 2>&1   # drain stdin so the caller never sees EPIPE

command -v jq >/dev/null 2>&1 || exit 0

jq -nc '{
  hookSpecificOutput: {
    hookEventName: "UserPromptSubmit",
    additionalContext: "Scout: if this request is a code task (fix / feature / refactor / diagnose), invoke the scout-pipeline skill BEFORE any edit; trivial fixes use its fast path. Scout is read-only by contract: any new call to an external system is a review lens, never a fast path. In that same announcement, name any other installed skill that applies to this task and say which you are using - or state explicitly that none apply. Do not skip a skill silently."
  }
}' 2>/dev/null

exit 0

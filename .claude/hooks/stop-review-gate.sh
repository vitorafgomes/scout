#!/bin/sh
# Stop hook: block the turn from ending when this session edited src/**/*.cs and never dispatched
# scout-reviewer (gate 6), either as an Agent dispatch or through the scout-review Workflow.
#
# Blocks once (since 2026-10-21, after WayUp_Backend/pulumi). Claude Code sets stop_hook_active on
# the continuation and that stop passes, so the agent either dispatches the reviewer or says in its
# reply why every edit was a fast-path one. No loop is possible.
#
# Keyed on what THIS session edited, read from the transcript and from the transcripts of the
# subagents it spawned (<session>/subagents/*.jsonl beside <session>.jsonl) - not on working-tree
# dirtiness, which would nag about a parallel session's files. The reviewer dispatch is looked for
# in the main transcript only, because the manager is the one that dispatches it.
#
# Fails open: any missing input, jq, or transcript exits 0 with no output.

INPUT=$(cat 2>/dev/null) || exit 0
[ -n "$INPUT" ] || exit 0

command -v jq >/dev/null 2>&1 || exit 0
ACTIVE=$(printf '%s' "$INPUT" | jq -r '.stop_hook_active // false' 2>/dev/null)
[ "$ACTIVE" = "true" ] && exit 0
TRANSCRIPT=$(printf '%s' "$INPUT" | jq -r '.transcript_path // ""' 2>/dev/null)

[ -n "$TRANSCRIPT" ] || exit 0
[ -f "$TRANSCRIPT" ] || exit 0

SUBAGENTS="${TRANSCRIPT%.jsonl}/subagents"
set -- "$TRANSCRIPT"
[ -d "$SUBAGENTS" ] && set -- "$@" "$SUBAGENTS"/*.jsonl

cat "$@" 2>/dev/null \
  | grep -E '"name":"(Edit|Write|NotebookEdit)"' \
  | grep -qE '"file_path":"[^"]*/src/[^"]*\.cs"' || exit 0

grep -qE '"subagent_type"[[:space:]]*:[[:space:]]*"scout-reviewer"' "$TRANSCRIPT" 2>/dev/null && exit 0
grep -qE '"name"[[:space:]]*:[[:space:]]*"scout-review"' "$TRANSCRIPT" 2>/dev/null && exit 0

jq -n '{
  decision: "block",
  reason: "Gate 6: this session edited src/**/*.cs but never dispatched scout-reviewer. Run the scout-review Workflow (or dispatch scout-reviewer) for the affected project now, or, if every edit was a fast-path one (single line, no logic/signature/query/DI impact), say so explicitly in your reply and stop again. See .claude/skills/scout-pipeline/SKILL.md."
}' 2>/dev/null

exit 0

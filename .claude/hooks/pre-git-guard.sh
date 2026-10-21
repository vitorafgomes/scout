#!/bin/sh
# PreToolUse hook (Bash): surface history-writing git, outward-facing gh, deploys and
# schema-applying EF commands as a permission ask (CLAUDE.md Rule E).
#
# "ask" (not deny) preserves the per-instance exception - "commit this for me" still works,
# it just cannot happen silently. Read-only git passes without a prompt.
#
# Fails open: bad JSON or a missing jq exits 0 with no output, so a broken hook can never
# block a tool call.

jq -c '
  (.tool_input.command // "") as $cmd
  | "\\bgit\\s+(?:-\\S+\\s+(?:(?!-)(?:[^\\s\"\\x27]|\"[^\"]*\"|\\x27[^\\x27]*\\x27)+\\s+)?)*" as $g
  | [
      ($g + "(commit|push|rebase|revert|cherry-pick|pull|am|filter-branch|update-ref|fast-import)\\b"),
      ($g + "merge\\b(?![^|;&]{0,200}--(abort|continue|quit))"),
      ($g + "reset\\b[^|;&]{0,200}(--hard|--soft|--merge|--keep|HEAD[~^]|@[~^]|\\b[0-9a-f]{7,40}\\b)"),
      ($g + "tag\\s+(-[adfsm]\\b|--delete\\b|--force\\b|(?!\\d+\\s*>)[^-\\s|&><;)(])"),
      ($g + "branch\\b[^|;&]{0,200}\\s(-f|--force|-D|-d|--delete|-m|--move)\\b"),
      ($g + "config\\s+[^|;&]{0,100}alias\\."),
      "\\bgh\\s+pr\\s+(merge|create)\\b",
      "\\bdocker\\s+(push|image\\s+push)\\b",
      "\\bwrangler\\s+(deploy|publish|secret)\\b",
      "\\bdotnet\\s+ef\\s+database\\s+(update|drop)\\b"
    ]
  | map(select(. as $re | $cmd | test($re; "i"))) | length > 0
  | if . then {
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "ask",
        permissionDecisionReason: "Rule E (CLAUDE.md): Claude never runs history-writing git, outward-facing gh, a deploy, or a command that applies schema to a live database - the human does. Approve only if you explicitly asked for this operation."
      }
    }
    else empty
    end
' 2>/dev/null

exit 0

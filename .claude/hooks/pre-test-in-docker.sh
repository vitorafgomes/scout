#!/bin/sh
# PreToolUse hook (Bash): tests run in the project's container, never on this machine.
#
# A command that starts a test run directly (dotnet test, the ui test scripts, vitest, playwright,
# test-per-project.sh) is denied, and the reason names the same command routed through
# in-docker.sh. A command already routed through in-docker.sh passes. Each part of a compound
# command is judged on its own, so `cd ui && bun run test` is caught too.
#
# Fails open: bad JSON or a missing jq exits 0 with no output, like every other hook here;
# session-start.sh is the one that says so out loud.

jq -c '
  "^\\s*(?:[A-Za-z_][A-Za-z0-9_]*=\\S*\\s+)*(?:(?:time|command|exec|nohup|rtk\\s+proxy)\\s+)*" as $lead
  | ("(?:dotnet\\s+test\\b|bun\\s+(?:run\\s+)?(?:test|e2e)(?:[:\\s]|$)|(?:bunx|npx|bun\\s+x)\\s+(?:vitest|playwright)\\b|(?:vitest|playwright)\\s|(?:(?:ba)?sh\\s+)?\\S*test-per-project\\.sh)") as $runner
  | (.tool_input.command // "") as $cmd
  | [$cmd | splits("[;&|\\n]+")]
  | map(select(test($lead + $runner; "i") and (test("in-docker\\.sh") | not)))
  | if length > 0 then {
      hookSpecificOutput: {
        hookEventName: "PreToolUse",
        permissionDecision: "deny",
        permissionDecisionReason: ("Tests run in the container, not on this machine. Run it as: tools/checks/in-docker.sh " + (.[0] | sub("^\\s+"; "") | sub("\\s+$"; "")))
      }
    } else empty end
' 2>/dev/null

exit 0

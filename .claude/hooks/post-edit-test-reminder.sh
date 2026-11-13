#!/bin/sh
# PostToolUse hook (Edit|Write): one short reminder after an edit to src/**/*.cs or to
# ui/packages/**/*.ts, naming the test that edit owes and the checks before a report.
#
# It fires on every such edit, so it is kept to two sentences; the pipeline skill has the detail.
#
# Fails open: bad JSON or a missing jq exits 0 with no output.

jq -c '
  (.tool_input.file_path // "") as $path
  | if ($path | test("(^|[\\\\/])src[\\\\/].+\\.cs$"; "i"))
    then "Edited code: write or run its test now (xUnit, Shouldly), through tools/checks/in-docker.sh. Before you report: build with 0 warnings, tools/checks/check-diagnostics.sh on the touched project, tests/Scout.ArchTests. A new call that leaves the process must be declared by its feature."
    elif ($path | test("(^|[\\\\/])ui[\\\\/]packages[\\\\/].+\\.(ts|mts)$"; "i") and ($path | test("\\.(spec|test)\\.(ts|mts)$"; "i") | not))
    then "Edited ui code: write or run its spec now. Before you report, in ui/: bun run typecheck, bun run lint, bun run format, and the tests through tools/checks/in-docker.sh."
    else empty
    end
  | { hookSpecificOutput: { hookEventName: "PostToolUse", additionalContext: . } }
' 2>/dev/null

exit 0

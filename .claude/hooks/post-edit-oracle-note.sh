#!/bin/sh
# PostToolUse hook (Edit|Write): note when a held-out oracle was edited.
#
# The build strictness files (Directory.Build.props, Directory.Packages.props, .editorconfig,
# global.json) and the CI workflows grade a change; they are not part of it. Editing them never asks,
# but the agent must say in its reply why the task required it, so a rule bent to pass a diff
# is visible.
#
# Fails open: bad JSON or a missing jq exits 0 with no output.

jq -c '
  (.tool_input.file_path // "") as $path
  | if ($path | test("(^|[\\\\/])(Directory\\.Build\\.props|Directory\\.Packages\\.props|\\.editorconfig|global\\.json|\\.github[\\\\/]workflows[\\\\/][^\\\\/]+)$"; "i"))
    then {
      hookSpecificOutput: {
        hookEventName: "PostToolUse",
        additionalContext: "Held-out oracle edited: Directory.Build.props, Directory.Packages.props, .editorconfig, global.json and .github/workflows/** grade the change, they are not part of it. State in your reply why this task required editing this file (a new analyzer, a reviewed package bump) and never weaken a rule to make a diff pass."
      }
    }
    else empty
    end
' 2>/dev/null

exit 0

#!/bin/sh
# PostToolUse hook (Edit|Write): one-line Rule B reminder on src/**/*.cs edits.
#
# Fails open: bad JSON or a missing jq exits 0 with no output.

jq -c '
  (.tool_input.file_path // "") as $path
  | if ($path | test("(^|[\\\\/])src[\\\\/].+\\.cs$"; "i"))
    then {
      hookSpecificOutput: {
        hookEventName: "PostToolUse",
        additionalContext: "Rule B: test this edit now (xUnit + Shouldly, never FluentAssertions). Then the gates: dotnet build Scout.slnx at 0 warnings (TreatWarningsAsErrors and EnforceCodeStyleInBuild are on), scripts/check-diagnostics.sh <touched.csproj> clean at every severity, dotnet format --verify-no-changes, and the test surface from scout-pipeline SKILL.md gate 4: a Contracts or shared-library edit is NOT covered by one test project. Rule A: if this edit adds a call to an external system, it must be a descriptive write (content a human reads), never an executive one."
      }
    }
    else empty
    end
' 2>/dev/null

exit 0

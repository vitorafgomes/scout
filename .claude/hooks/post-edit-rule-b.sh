#!/bin/sh
# PostToolUse hook (Edit|Write): one-line Rule B reminder on src/**/*.cs and ui/packages/**/*.ts edits.
#
# Fails open: bad JSON or a missing jq exits 0 with no output.

jq -c '
  (.tool_input.file_path // "") as $path
  | if ($path | test("(^|[\\\\/])src[\\\\/].+\\.cs$"; "i"))
    then {
      hookSpecificOutput: {
        hookEventName: "PostToolUse",
        additionalContext: "Rule B: test this edit now (xUnit + Shouldly, never FluentAssertions). Then the gates: dotnet build Scout.slnx at 0 warnings (TreatWarningsAsErrors, AnalysisMode=Recommended, EnforceCodeStyleInBuild), scripts/check-diagnostics.sh <touched.csproj> clean at every severity, dotnet test tests/Scout.ArchTests green, dotnet format Scout.slnx --verify-no-changes (under the SDK that opens the workspace; tooling page), and the test surface from scout-pipeline SKILL.md gate 4: a Contracts or shared-library edit is NOT covered by one test project. Rule A: if this edit adds a call to an external system, it runs only behind a declared, person-approved action (ADR 0032), never as an executive write of its own."
      }
    }
    elif ($path | test("(^|[\\\\/])ui[\\\\/]packages[\\\\/].+\\.(ts|mts)$"; "i") and ($path | test("\\.(spec|test)\\.(ts|mts)$"; "i") | not))
    then {
      hookSpecificOutput: {
        hookEventName: "PostToolUse",
        additionalContext: "Rule B (ui): a spec for this edit now (vitest in renderer, bun test in main, preload and shared), then in ui/: bun run typecheck, bun run lint, bun run format, bun run test (per package counts), bun run build at 0 warnings."
      }
    }
    else empty
    end
' 2>/dev/null

exit 0

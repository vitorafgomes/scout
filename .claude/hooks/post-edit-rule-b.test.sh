#!/usr/bin/env bash
# Case matrix for post-edit-rule-b.sh: CS = the backend reminder, UI = the ui reminder, QUIET = nothing.
HOOK="${1:-$(dirname "$0")/post-edit-rule-b.sh}"
fail=0
check() {
  want=$1; path=$2
  out=$(printf '{"tool_input":{"file_path":"%s"}}' "$path" | sh "$HOOK")
  if printf '%s' "$out" | grep -q '"Rule B (ui)'; then actual=UI
  elif printf '%s' "$out" | grep -q '"Rule B:'; then actual=CS
  else actual=QUIET; fi
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $path"; fail=1; fi
}
for p in "/repo/src/Scout.Knowledge/ProjectFileFilter.cs" 'src\\Scout.Host.Console\\Program.cs'; do check CS "$p"; done
for p in "/repo/ui/packages/renderer/src/app/github-connect.component.ts" "/repo/ui/packages/main/src/updater.mts"; do check UI "$p"; done
for p in "/repo/ui/packages/renderer/src/app/github-connect.component.spec.ts" "/repo/ui/packages/main/src/updater.test.ts" \
         "/repo/ui/packages/renderer/src/app/github-connect.component.html" "/repo/tests/Scout.Knowledge.Tests/Foo.cs" \
         "/repo/vault/log.md" "/repo/.claude/hooks/post-edit-rule-b.sh" "/repo/ui/eslint.config.js" ""; do check QUIET "$p"; done
# The backend reminder names the gates it exists for.
out=$(printf '{"tool_input":{"file_path":"/repo/src/Scout.Llm/Foo.cs"}}' | sh "$HOOK")
for needle in "check-diagnostics" "Scout.ArchTests" "Shouldly" "Rule A"; do
  printf '%s' "$out" | grep -q "$needle" || { echo "FAIL backend reminder lacks: $needle"; fail=1; }
done
printf '%s' "$out" | jq -e '.hookSpecificOutput.hookEventName == "PostToolUse"' >/dev/null || { echo "FAIL output is not the PostToolUse shape"; fail=1; }
# Bad input fails open: nothing written, exit 0.
out=$(printf 'not json' | sh "$HOOK"); rc=$?
if [ -n "$out" ] || [ "$rc" != "0" ]; then echo "FAIL bad input must be silent and exit 0"; fail=1; fi
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

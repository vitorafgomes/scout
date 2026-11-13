#!/usr/bin/env bash
# Case matrix for post-edit-build-rules-note.sh: NOTE = must emit the note, QUIET = must emit nothing.
HOOK="${1:-$(dirname "$0")/post-edit-build-rules-note.sh}"
fail=0
check() {
  want=$1; path=$2
  got=$(printf '{"tool_input":{"file_path":"%s"}}' "$path" | sh "$HOOK" | grep -c "Build rules edited")
  [ "$got" = "1" ] && actual=NOTE || actual=QUIET
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $path"; fail=1; fi
}
for p in "/repo/Directory.Build.props" "Directory.Packages.props" "/repo/.editorconfig" "/repo/global.json" \
         "/repo/.github/workflows/ci.yml" 'C:\\repo\\.github\\workflows\\release.yml' "/repo/GLOBAL.JSON"; do check NOTE "$p"; done
for p in "/repo/src/Scout.Llm/LlmOptions.cs" "/repo/tests/Scout.Llm.Tests/LlmOptionsTests.cs" \
         "/repo/context/pages/services/llm.md" "/repo/ui/package.json" "/repo/.github/PULL_REQUEST_TEMPLATE.md" \
         "/repo/src/Scout.Host.Console/global.json.bak" "/repo/Directory.Other.props" ""; do check QUIET "$p"; done
out=$(printf 'not json' | sh "$HOOK"; echo "rc=$?")
[ "$out" = "rc=0" ] || { echo "FAIL bad input must be silent and exit 0 :: $out"; fail=1; }
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

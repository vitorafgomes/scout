#!/usr/bin/env bash
# Case matrix for pre-test-in-docker.sh: DENY = must refuse and name in-docker.sh, PASS = must emit nothing.
HOOK="${1:-$(dirname "$0")/pre-test-in-docker.sh}"
fail=0
check() {
  want=$1; cmd=$2
  out=$(jq -nc --arg c "$cmd" '{tool_input:{command:$c}}' | sh "$HOOK")
  if printf '%s' "$out" | grep -q '"permissionDecision":"deny"'; then actual=DENY; else actual=PASS; fi
  [ "$actual" = "$want" ] || { echo "FAIL want=$want got=$actual :: $cmd"; fail=1; }
}
while IFS= read -r c; do [ -n "$c" ] && check DENY "$c"; done <<'CASES'
dotnet test
dotnet test tests/Scout.Llm.Tests --no-build
DOTNET_NOLOGO=1 dotnet test tests/Scout.ArchTests
cd ui && bun run test
bun test
bun run test:renderer
bun run e2e
bunx vitest run
npx playwright test
bash tools/checks/test-per-project.sh --no-build
tools/checks/test-per-project.sh
dotnet build Scout.slnx && dotnet test tests/Scout.ArchTests
rtk proxy dotnet test tests/Scout.Llm.Tests
tools/checks/in-docker.sh dotnet build Scout.slnx; dotnet test
CASES
while IFS= read -r c; do [ -n "$c" ] && check PASS "$c"; done <<'CASES'
tools/checks/in-docker.sh dotnet test tests/Scout.Llm.Tests
tools/checks/in-docker.sh bash tools/checks/test-per-project.sh
cd ui && ../tools/checks/in-docker.sh bun run test
dotnet build Scout.slnx
dotnet format Scout.slnx --verify-no-changes
bun run typecheck
bun run lint
git status
grep -rn "dotnet test" docs
echo "run dotnet test in the container"
bash tools/dev/token-usage.test.sh
CASES
reason=$(jq -nc '{tool_input:{command:"cd ui && bun run test"}}' | sh "$HOOK" | jq -r '.hookSpecificOutput.permissionDecisionReason')
[ "$reason" = "Tests run in the container, not on this machine. Run it as: tools/checks/in-docker.sh bun run test" ] || { echo "FAIL the reason must name the command to run instead :: $reason"; fail=1; }
out=$(printf 'not json' | sh "$HOOK"; echo "rc=$?"); [ "$out" = "rc=0" ] || { echo "FAIL bad input must be silent and exit 0 :: $out"; fail=1; }
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

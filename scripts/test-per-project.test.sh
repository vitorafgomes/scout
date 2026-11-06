#!/usr/bin/env bash
# Case matrix for test-per-project.sh, run against a fake `dotnet` so no build happens: PASS = exit 0,
# FAIL = exit 1 and the verdict named.
SCRIPT="$(cd "$(dirname "$0")" && pwd)/test-per-project.sh"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
fail=0
mkdir -p "$work/repo/scripts" "$work/repo/tests/A.Tests" "$work/repo/tests/B.Tests" "$work/bin"
cp "$SCRIPT" "$work/repo/scripts/"; touch "$work/repo/tests/A.Tests/A.Tests.csproj" "$work/repo/tests/B.Tests/B.Tests.csproj"
# The fake reads one summary line per project from $FAKE_DIR/<name>.out; a missing file is a crash.
cat > "$work/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
name=$(basename "$2" .csproj); f="$FAKE_DIR/$name.out"
[ -f "$f" ] || { echo "MSBUILD : error : $name did not compile"; exit 1; }
cat "$f"; grep -q '^Failed!' "$f" && exit 1; exit 0
FAKE
chmod +x "$work/bin/dotnet"
run() { want=$1; name=$2; expect=${3:-}
  out=$(cd "$work/repo" && PATH="$work/bin:$PATH" FAKE_DIR="$work/fake" bash scripts/test-per-project.sh 2>&1); rc=$?
  [ "$rc" = "0" ] && actual=PASS || actual=FAIL
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $name"; echo "$out" | sed 's/^/    /'; fail=1; fi
  if [ -n "$expect" ] && ! printf '%s' "$out" | grep -q "$expect"; then echo "FAIL output lacks '$expect' :: $name"; fail=1; fi
}
reset() { rm -rf "$work/fake"; mkdir -p "$work/fake"; }
reset; echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12, Duration: 1 s" > "$work/fake/A.Tests.out"; cp "$work/fake/A.Tests.out" "$work/fake/B.Tests.out"
run PASS "two green projects" "PASS         B.Tests"
reset; echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12" > "$work/fake/A.Tests.out"; echo "Failed!  - Failed:     1, Passed:    11, Skipped:     0, Total:    12" > "$work/fake/B.Tests.out"
run FAIL "one failing project" "FAIL         B.Tests"
reset; echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12" > "$work/fake/A.Tests.out"; echo "Passed!  - Failed:     0, Passed:    11, Skipped:     1, Total:    12" > "$work/fake/B.Tests.out"
run FAIL "a skipped test fails the gate" "skipped=1"
reset; echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12" > "$work/fake/A.Tests.out"
run FAIL "a project that did not compile" "DID-NOT-RUN  B.Tests"
reset; echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12" > "$work/fake/A.Tests.out"; echo "Passed!  - Failed:     0, Passed:     0, Skipped:     0, Total:     0" > "$work/fake/B.Tests.out"
run FAIL "a project that ran zero tests" "DID-NOT-RUN  B.Tests"
reset; echo "Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12" > "$work/fake/A.Tests.out"; echo "Passed!  - Failed: 0, Passed: 12, Total: 12 (no skipped field)" > "$work/fake/B.Tests.out"
run FAIL "a summary line the script cannot parse" "PARSE-ERROR   B.Tests"
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

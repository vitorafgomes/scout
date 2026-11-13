#!/usr/bin/env bash
# Case matrix for test-per-project.sh, run against a fake `dotnet` so no build happens: PASS = exit 0,
# FAIL = exit 1 and the verdict named.
SCRIPT="$(cd "$(dirname "$0")" && pwd)/test-per-project.sh"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
fail=0
mkdir -p "$work/repo/tools/checks" "$work/repo/tests/A.Tests" "$work/repo/tests/B.Tests" "$work/bin"
cp "$SCRIPT" "$work/repo/tools/checks/"; touch "$work/repo/tests/A.Tests/A.Tests.csproj" "$work/repo/tests/B.Tests/B.Tests.csproj"
# The fake reads one summary line per project from $FAKE_DIR/<name>.out; a missing file is a crash.
cat > "$work/bin/dotnet" <<'FAKE'
#!/usr/bin/env bash
name=$(basename "$2" .csproj); f="$FAKE_DIR/$name.out"; echo "$*" > "$FAKE_DIR/$name.args"
[ -f "$f" ] || { echo "MSBUILD : error : $name did not compile"; exit 1; }
cat "$f"; grep -q '^Failed!' "$f" && exit 1; exit 0
FAKE
cat > "$work/bin/docker" <<'FAKE'
#!/usr/bin/env bash
exit "${FAKE_DOCKER_RC:-0}"
FAKE
chmod +x "$work/bin/dotnet" "$work/bin/docker"
run() { want=$1; name=$2; expect=${3:-}
  out=$(cd "$work/repo" && PATH="$work/bin:$PATH" FAKE_DIR="$work/fake" bash tools/checks/test-per-project.sh 2>&1); rc=$?
  [ "$rc" = "0" ] && actual=PASS || actual=FAIL
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $name"; echo "$out" | sed 's/^/    /'; fail=1; fi
  if [ -n "$expect" ] && ! printf '%s' "$out" | grep -q "$expect"; then echo "FAIL output lacks '$expect' :: $name"; fail=1; fi
}
reset() { rm -rf "$work/fake" "$work/repo/tests/Scout.Sandbox.Tests"; mkdir -p "$work/fake"; unset FAKE_DOCKER_RC; }
green="Passed!  - Failed:     0, Passed:    12, Skipped:     0, Total:    12"
sandbox() { mkdir -p "$work/repo/tests/Scout.Sandbox.Tests"; touch "$work/repo/tests/Scout.Sandbox.Tests/Scout.Sandbox.Tests.csproj"
  for p in A.Tests B.Tests Scout.Sandbox.Tests; do echo "$green" > "$work/fake/$p.out"; done; }
args_has() { if ! grep -q -- "$2" "$work/fake/$1.args"; then echo "FAIL $1 was not run with '$2' :: $3"; fail=1; fi; }
args_lacks() { if grep -q -- "$2" "$work/fake/$1.args"; then echo "FAIL $1 was run with '$2' :: $3"; fail=1; fi; }
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
reset; sandbox; export FAKE_DOCKER_RC=1
run PASS "no Docker daemon: the Docker tests are filtered out and the script says so" "Docker is not reachable; the Docker end-to-end tests did not run."
args_has Scout.Sandbox.Tests "--filter Category!=Docker" "no Docker daemon"
args_lacks A.Tests "Category!=Docker" "no Docker daemon, another project"
reset; sandbox; export FAKE_DOCKER_RC=0
run PASS "a Docker daemon: every Sandbox test runs"
args_lacks Scout.Sandbox.Tests "Category!=Docker" "a Docker daemon"
if printf '%s' "$out" | grep -q "Docker is not reachable"; then echo "FAIL the notice printed with a Docker daemon"; fail=1; fi
reset; mkdir -p "$work/bare/tools/checks"; cp "$SCRIPT" "$work/bare/tools/checks/"
out=$(cd "$work/bare" && PATH="$work/bin:$PATH" FAKE_DIR="$work/fake" bash tools/checks/test-per-project.sh 2>&1); rc=$?
[ "$rc" = 1 ] && printf '%s' "$out" | grep -q "DID-NOT-RUN  no test project found" || { echo "FAIL want FAIL and the notice, no tests/ folder: rc=$rc out=$out"; fail=1; }
mkdir -p "$work/bare/tests/Empty"
out=$(cd "$work/bare" && PATH="$work/bin:$PATH" FAKE_DIR="$work/fake" bash tools/checks/test-per-project.sh 2>&1); rc=$?
[ "$rc" = 1 ] && printf '%s' "$out" | grep -q "DID-NOT-RUN  no test project found" || { echo "FAIL want FAIL and the notice, a tests/ folder with no project: rc=$rc out=$out"; fail=1; }
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

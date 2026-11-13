#!/usr/bin/env bash
# Case matrix for with-test-version.sh in a throwaway repository with a copy of ui/package.json and a fake build
# command that records the version it sees: the build gets a test version above the last release, and
# package.json comes back byte for byte whether the build passes, fails or is killed.
DIR="$(cd "$(dirname "$0")" && pwd)"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
fail=0
g() { git -C "$work/repo" -c user.name=t -c user.email=t@t -c commit.gpgsign=false -c tag.gpgsign=false -c core.hooksPath=/dev/null "$@"; }
mkdir -p "$work/repo/tools/release" "$work/repo/ui" "$work/bin"
cp "$DIR/next-version.sh" "$DIR/with-test-version.sh" "$work/repo/tools/release/"
cp "$DIR/../../ui/package.json" "$work/repo/ui/package.json"; cp "$work/repo/ui/package.json" "$work/original.json"
git init -q -b main "$work/repo"; g add -A; g commit -q -m "chore: start"; g tag v1.2.3; g commit -q --allow-empty -m "feat: a read"
sha=$(g rev-parse --short HEAD)
cat > "$work/bin/fake-dist" <<'FAKE'
#!/usr/bin/env bash
node -e 'console.log(require("./package.json").version)' > "$FAKE_SEEN"
case "$1" in fail) exit 3 ;; killed) kill -TERM "$PPID"; sleep 5 ;; esac
exit 0
FAKE
chmod +x "$work/bin/fake-dist"
run() { rm -f "$work/seen"; out=$(cd "$work/repo/ui" && PATH="$work/bin:$PATH" FAKE_SEEN="$work/seen" bash ../tools/release/with-test-version.sh "$@" 2>&1); rc=$?; }
check() { name=$1; want_rc=$2
  { [ "$rc" = "$want_rc" ] || { [ "$want_rc" = nonzero ] && [ "$rc" != 0 ]; }; } || { echo "FAIL want exit $want_rc got $rc :: $name"; echo "$out" | sed 's/^/    /'; fail=1; }
  cmp -s "$work/original.json" "$work/repo/ui/package.json" || { echo "FAIL ui/package.json was not restored byte for byte :: $name"; fail=1; }; }
saw_test_version() { seen=$(cat "$work/seen" 2>/dev/null)
  [[ $seen =~ ^1\.3\.0-test\.[0-9]{12}\.g$sha$ ]] || { echo "FAIL the build saw version '$seen', not a test version above v1.2.3 :: $1"; fail=1; }; }

run fake-dist
check "a passing build" 0; saw_test_version "a passing build"
printf '%s' "$out" | grep -q "Building Scout Desktop 1.3.0-test." || { echo "FAIL the version is not announced"; fail=1; }
run fake-dist fail
check "a failing build keeps its exit code and restores package.json" 3; saw_test_version "a failing build"
run fake-dist killed
check "a killed build restores package.json" 143; saw_test_version "a killed build"
run
check "no command prints the usage" 2
mv "$work/repo/.git" "$work/git"
run fake-dist
check "outside a git repository the build never runs" nonzero
[ -f "$work/seen" ] && { echo "FAIL the build ran without a version"; fail=1; }
mv "$work/git" "$work/repo/.git"
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

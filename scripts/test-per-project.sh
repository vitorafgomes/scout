#!/usr/bin/env bash
# Run every test project under tests/ one at a time and fail when any project fails, skips a test,
# or runs nothing at all. A solution-wide `dotnet test` reports one green total, which has hidden a
# project that did not compile and a suite that was skipped entirely; the pipeline's gate 4 asks for
# `N passed` per project and a Skipped count of 0, and this script is that gate in one command.
#
# Scout.Sandbox.Tests carries end-to-end tests (trait Category=Docker) that need a Docker daemon; when
# `docker version` gets no answer from one, that project runs with --filter Category!=Docker and the
# script says so. CI runs the Docker tests in a step of their own, so there they always run.
#
# Usage: scripts/test-per-project.sh [extra dotnet test args, e.g. --configuration Release --no-build]
set -uo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
export MSBUILDDISABLENODEREUSE=1
fail=0
docker_filter=()
if ! docker version --format '{{.Server.Version}}' >/dev/null 2>&1; then
  docker_filter=(--filter 'Category!=Docker')
  echo "Docker is not reachable; the Docker end-to-end tests did not run."
fi
while IFS= read -r -d '' project; do
  name=$(basename "$project" .csproj)
  extra=()
  [ "$name" = "Scout.Sandbox.Tests" ] && extra=(${docker_filter[@]+"${docker_filter[@]}"})
  out=$(dotnet test "$project" "$@" ${extra[@]+"${extra[@]}"} 2>&1); rc=$?
  summary=$(printf '%s\n' "$out" | grep -E '^(Passed|Failed)!.*Total:' | tail -n 1)
  if [ -z "$summary" ]; then
    printf '%s\n' "$out" | tail -n 20
    echo "DID-NOT-RUN  $name (exit $rc, no test summary line)"; fail=1; continue
  fi
  passed=$(printf '%s' "$summary" | sed -E 's/.*Passed:[[:space:]]*([0-9]+).*/\1/')
  failed=$(printf '%s' "$summary" | sed -E 's/.*Failed:[[:space:]]*([0-9]+).*/\1/')
  skipped=$(printf '%s' "$summary" | sed -E 's/.*Skipped:[[:space:]]*([0-9]+).*/\1/')
  for n in "$passed" "$failed" "$skipped"; do
    [[ "$n" =~ ^[0-9]+$ ]] || { echo "PARSE-ERROR   $name: unexpected summary line: $summary"; fail=1; continue 2; }
  done
  verdict=PASS
  if [ "$rc" != "0" ] || [ "$failed" != "0" ]; then verdict=FAIL; fail=1; printf '%s\n' "$out" | grep -E '^\s+(Failed|Error Message|Stack Trace|at )|\[FAIL\]' | head -n 60; fi
  if [ "$skipped" != "0" ]; then verdict=FAIL; fail=1; echo "  a skipped test reports green while executing nothing"; fi
  if [ "$passed" = "0" ]; then verdict=DID-NOT-RUN; fail=1; fi
  printf '%-12s %s  passed=%s failed=%s skipped=%s\n' "$verdict" "$name" "$passed" "$failed" "$skipped"
done < <(find "$root/tests" -name '*.csproj' -not -path '*/obj/*' -not -path '*/bin/*' -print0 | sort -z)
exit $fail

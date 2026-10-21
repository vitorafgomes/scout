#!/bin/sh
# Usage: scripts/check-diagnostics.sh [<csproj|sln|->] [info|warning|error]
#
#   <target>  project or solution to check (default: Scout.slnx).
#             Pass a single .csproj for speed; a detailed rebuild of the sln is slow.
#             Pass - to parse a build log on stdin instead of running dotnet.
#   <minsev>  lowest severity that fails the run (default: info).
#
# Why it exists: this repo already builds with TreatWarningsAsErrors and EnforceCodeStyleInBuild,
# but test projects do not generate a documentation file, and without it IDE0005 (unused usings)
# is never emitted (dotnet/roslyn#41640); info-level diagnostics never fail a build either. This
# forces both on, rebuilds at detailed verbosity and fails on anything left. CLAUDE.md Rule B.

set -u

TARGET="${1:-Scout.slnx}"
MINSEV="${2:-info}"

case "$MINSEV" in
  info|warning|error) ;;
  *) echo "check-diagnostics: minsev must be info, warning or error (got '$MINSEV')" >&2; exit 2 ;;
esac

MSBUILDDISABLENODEREUSE=1
export MSBUILDDISABLENODEREUSE

LOG=$(mktemp "${TMPDIR:-/tmp}/check-diagnostics.XXXXXX") || exit 2
trap 'rm -f "$LOG"' EXIT INT TERM

if [ "$TARGET" = "-" ]; then
  cat >"$LOG"
  BUILD_RC=0
else
  if ! command -v dotnet >/dev/null 2>&1; then
    echo "check-diagnostics: dotnet not found on PATH" >&2
    exit 2
  fi
  echo "Forced rebuild of $TARGET at detailed verbosity (slow but complete)..."
  # GenerateDocumentationFile=true is not cosmetic: IDE0005 is not emitted during a build
  # without it (dotnet/roslyn#41640), and this repo's Directory.Build.props sets it only for
  # non-test projects. NoWarn=CS1591 keeps the doc file from turning every undocumented public
  # member into a build error under TreatWarningsAsErrors.
  dotnet build "$TARGET" -t:Rebuild -v detailed \
    -p:UseSharedCompilation=false -p:EnforceCodeStyleInBuild=true \
    -p:GenerateDocumentationFile=true -p:NoWarn=CS1591 --nologo >"$LOG" 2>&1
  BUILD_RC=$?
fi

awk -v minsev="$MINSEV" '
function rank(s) { return s == "error" ? 2 : (s == "warning" ? 1 : 0) }
{
  if (!match($0, /\([0-9]+,[0-9]+\):[ \t]+(error|warning|message|info)?[ \t]*[A-Z][A-Z]+[0-9][0-9][0-9]+/)) next

  seg = substr($0, RSTART, RLENGTH)
  pre = substr($0, 1, RSTART - 1)

  n = split(pre, parts, /[ \t]+/)
  file = parts[n]
  sub(/.*[\/\\]/, "", file)

  lno = seg; sub(/^\(/, "", lno); sub(/,.*/, "", lno)

  code = seg; sub(/.*[ \t]/, "", code)

  if (seg ~ /\):[ \t]*error[ \t]/)        sev = "error"
  else if (seg ~ /\):[ \t]*warning[ \t]/) sev = "warning"
  else                                     sev = "info"

  key = code "|" file "|" lno
  if (key in seen) next
  seen[key] = 1

  count[code]++
  if (!(code in worst) || rank(sev) > rank(worst[code])) worst[code] = sev
  if (!(code in firstfile)) { firstfile[code] = file; firstline[code] = lno }
  total++
  if (rank(sev) >= rank(minsev)) fails++
}
END {
  if (total == 0) { print "No diagnostics found (any code, info/warning/error)."; exit 0 }

  printf "\nDiagnostics (%d distinct):\n", total
  for (c in count)
    printf "  %-10s %-8s x%-4d first: %s:%s\n", c, worst[c], count[c], firstfile[c], firstline[c]

  if (fails > 0) {
    printf "\n%d diagnostic(s) at or above %s -- failing.\n", fails, minsev
    exit 1
  }
  exit 0
}
' "$LOG"
AWK_RC=$?

if [ "$AWK_RC" -ne 0 ]; then
  exit "$AWK_RC"
fi

if [ "$BUILD_RC" -ne 0 ]; then
  echo "check-diagnostics: no diagnostic matched, but dotnet build exited $BUILD_RC -- failing." >&2
  tail -n 20 "$LOG" >&2
  exit 1
fi

exit 0

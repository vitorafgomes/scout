#!/usr/bin/env bash
# Resolve every `path:line` citation in vault/pages against the working tree.
#
# A citation is src|tests|ui|scripts|.claude|.github/<path>.<ext>:<line>[-<line>], written in full; an
# abbreviated path (`…`) or a bare continuation (`:15`) is not a citation to this script. It fails
# when the file is missing or the first line is past the end of the file. It proves the line exists, never
# that it still describes the claim; that part is read. Pages whose frontmatter status marks them
# as a record (superseded, archived, artefact, resolved, deprecated) are skipped, and so is a path
# that names another repo (`other-repo/src/...`).
#
# Usage: scripts/check-vault-refs.sh [root]   exits 1 on any broken ref.
set -uo pipefail
root="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
total=0; skipped=0; broken=0
while IFS= read -r -d '' page; do
  if awk 'NR==1 && $0!="---"{exit} NR>1 && $0=="---"{exit} NR>1' "$page" | grep -qiE '^status:[[:space:]]*["'"'"']?(superseded|archived|artefact|deprecated|resolved|snapshot|decommissioned|wont-fix)\b'; then
    skipped=$((skipped+1)); continue
  fi
  rel="${page#"$root"/}"
  while IFS=: read -r file line; do
    [ -n "$file" ] || continue
    total=$((total+1))
    target="$root/$file"
    if [ ! -f "$target" ]; then
      echo "$rel: $file:$line - file missing"; broken=$((broken+1))
    else
      count=$(awk 'END{print NR}' "$target")
      if [ "$line" -gt "$count" ]; then
        echo "$rel: $file:$line - file has $count lines"; broken=$((broken+1))
      fi
    fi
  done < <(grep -oE '(^|[^A-Za-z0-9_./-])(src|tests|ui|scripts|\.claude|\.github)/[A-Za-z0-9_./-]+\.[A-Za-z0-9]+:[0-9]+' "$page" | sed -E 's/^[^A-Za-z0-9_.]//' | sort -u)
done < <(find "$root/vault/pages" -name '*.md' -print0)
if [ "$broken" -gt 0 ]; then
  echo "$broken of $total vault file:line citations do not resolve." >&2
  exit 1
fi
echo "$total vault file:line citations resolve ($skipped record pages skipped)."

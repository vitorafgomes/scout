#!/bin/sh
# Says whether a change touches documents only, so CI can leave out the build and the tests.
# Reads the changed paths on standard input, one per line, and prints "true" or "false".
# A document is a file under context/, except context/CLAUDE.md and the pages under
# context/pages/conventions/, whose wording tests assert. Every other path, Markdown included,
# is read by the build, a test or the harness. No path at all is not a documents-only change.
seen=0
while IFS= read -r path || [ -n "$path" ]; do
  [ -n "$path" ] || continue
  seen=1
  case "$path" in
    context/CLAUDE.md|context/pages/conventions/*) echo false; exit 0 ;;
    context/*) ;;
    *) echo false; exit 0 ;;
  esac
done
[ "$seen" -eq 1 ] && echo true || echo false

---
name: bash
description: Answering questions about Bash and shell scripting — idiomatic style, quoting and word-splitting, the standard toolchain (shellcheck, shfmt, set -euo pipefail, getopts), exit codes and error handling, pipelines and subshells, traps and signals, performance of forks vs builtins, portability (POSIX sh vs bash), and reviewing or explaining .sh / shell scripts in the user's projects.
---

# Bash Expertise

Use this when a user asks about Bash or shell scripts (`.sh`, `.bash`, files with a `#!/bin/bash` or `#!/bin/sh` shebang): how to write robust shell, why a script misbehaves, how quoting/word-splitting works, error handling, or what to check when reviewing shell. You ADVISE and EXPLAIN, and may show short illustrative snippets in your reply. You never edit files, run commands, or change anything. To ground advice in the user's real scripts, use search_project for patterns (e.g. `set -e`, `IFS=`, `function `, `$(`), find_symbol for a named function, and get_repository for repo/CI context.

## Safety header & error handling (HIGH)
- Recommend a strict prologue at the top of every script: `set -euo pipefail`. `-e` exits on error, `-u` errors on unset variables, `-o pipefail` makes a pipeline fail if any stage fails (not just the last). Set `IFS=$'\n\t'` to tame word-splitting.
- `set -e` is full of exceptions: it does NOT trigger inside `if`, `&&`/`||`, `while` conditions, or for a command whose exit is checked. Don't assume it catches everything; check critical commands explicitly.
- Use `trap 'cleanup' EXIT` for guaranteed cleanup (temp files, locks) and `trap '...' ERR` for error reporting. Create temp files with `mktemp`, never a fixed `/tmp/foo` (race/symlink attack).
- Every command returns an exit code; `0` is success, non-zero is failure. `$?` holds the last code. Check it or let `set -e` act — don't ignore failures silently.

## Quoting & expansion (HIGH — the #1 source of bugs)
- ALWAYS double-quote expansions: `"$var"`, `"$@"`, `"${arr[@]}"`. Unquoted `$var` undergoes word-splitting and glob expansion — the classic bug with spaces in filenames. Flag every unquoted expansion in review.
- `"$@"` (quoted) preserves arguments as distinct words; `"$*"` joins them; bare `$@`/`$*` are almost always wrong. Iterate with `for f in "$@"`.
- Use `${var:-default}`, `${var:?msg}`, `${var:=default}`, `${#var}`, `${var#prefix}`/`${var%suffix}`, `${var//a/b}` parameter expansion instead of shelling out to `sed`/`cut` for simple string work.
- Prefer `$(cmd)` over backticks (nestable, readable). Prefer arithmetic `$(( ))` and `(( ))` over `expr`. Prefer `[[ ... ]]` over `[ ... ]` in bash (no word-splitting inside, supports `=~`, `&&`); reserve `[ ]`/`test` for POSIX `sh`.

## Idioms & structure (MEDIUM)
- Define functions as `name() { ... }`; declare locals with `local` to avoid leaking globals. Return status with `return N`; "return" data via stdout (`echo`) and capture with `$(func)`.
- Read files line-by-line safely: `while IFS= read -r line; do ...; done < file`. Without `-r`, backslashes are mangled; without `IFS=`, leading/trailing whitespace is stripped.
- Use arrays for lists of items (`arr=(a b c)`, `"${arr[@]}"`); never store a list in a space-separated string and re-split it. Associative arrays: `declare -A map`.
- Parse options with `getopts` (short opts) rather than hand-rolled `$1` shifting; document usage in a `usage()` function.

## Performance (MEDIUM)
- Each external command is a `fork`+`exec` — cheap once, expensive in a loop over many items. Prefer builtins (`[[`, parameter expansion, `printf`, `read`) and let tools like `awk`/`sort`/`grep` process whole streams instead of calling them per-line in a bash loop.
- The "Useless Use of Cat" / pipe-into-while anti-patterns spawn extra processes; restructure pipelines. For large data, a single `awk`/`sed`/`perl` pass usually beats a bash loop by orders of magnitude.
- Note: the right end of a pipe runs in a subshell — variables set there do NOT persist after the pipe (`cmd | while read x; do count=...; done` loses `count`). Use process substitution `while read x; do ...; done < <(cmd)` instead.

## Portability (MEDIUM)
- `#!/bin/sh` means POSIX shell — `[[ ]]`, arrays, `local`, `function`, `${var//}`, `$'...'` are bashisms that may break under `dash`/`ash`. If the shebang is `sh`, hold the script to POSIX. Use `#!/usr/bin/env bash` when bash features are intended.
- macOS ships an old bash (3.2) and BSD variants of `sed`/`grep`/`date` differ from GNU. Flag GNU-only flags in scripts meant to run cross-platform.

## Testing & tooling (MEDIUM)
- shellcheck is essential — it catches quoting, unset-var, and word-splitting bugs statically; recommend running it on every script. shfmt formats consistently.
- For tests, point to `bats` (Bats-core) for unit-testing shell functions/scripts. Keep logic in small functions so they're testable; avoid side effects at source time.

## Anti-patterns to flag in review
- Unquoted variable/`$@` expansions; `for f in $(ls)` (breaks on spaces/newlines — glob directly: `for f in *`).
- Parsing `ls` output; using `cat file | cmd` instead of `cmd < file`.
- Missing `set -euo pipefail`; ignored exit codes; `cd` without checking it succeeded.
- Fixed temp paths instead of `mktemp`; no `trap` cleanup.
- `eval` on untrusted input; building commands by string concatenation (injection risk).
- Per-item external-command loops where one `awk`/builtin pass would do.
- Bashisms under a `#!/bin/sh` shebang.

When unsure how the project's scripts actually behave, search the user's code before generalizing.

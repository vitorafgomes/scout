#!/usr/bin/env bash
# Case matrix for stop-review-gate.sh: WARN = must emit the gate 6 block, QUIET = must emit nothing.
HOOK="${1:-$(dirname "$0")/stop-review-gate.sh}"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
fail=0
edit_src='{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/repo/src/Modules/X/Foo.cs"}}]}}'
edit_doc='{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"/repo/vault/log.md"}}]}}'
review='{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Agent","input":{"subagent_type":"scout-reviewer"}}]}}'
workflow='{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Workflow","input":{"name":"scout-review","args":{"module":"Management"}}}]}}'
check() {
  want=$1; name=$2; dir=$3; extra=${4:-}
  got=$(printf '{"transcript_path":"%s"%s}' "$dir/session.jsonl" "$extra" | sh "$HOOK" | grep -c '"decision": "block"')
  [ "$got" = "1" ] && actual=WARN || actual=QUIET
  if [ "$actual" != "$want" ]; then echo "FAIL want=$want got=$actual :: $name"; fail=1; fi
}
# Real layout: <project>/<session>.jsonl with subagents under <project>/<session>/subagents/.
# (a) src edit only inside a subagent transcript, no reviewer
a=$work/a; mkdir -p $a/session/subagents
echo "$edit_doc" > $a/session.jsonl; echo "$edit_src" > $a/session/subagents/agent-1.jsonl
check WARN "subagent edited src, no reviewer" $a
# (b) same, plus a reviewer dispatch in the main transcript
b=$work/b; mkdir -p $b/session/subagents
printf '%s\n%s\n' "$edit_doc" "$review" > $b/session.jsonl; echo "$edit_src" > $b/session/subagents/agent-1.jsonl
check QUIET "subagent edited src, reviewer dispatched by main" $b
# (c) no src edit anywhere
c=$work/c; mkdir -p $c/session/subagents
echo "$edit_doc" > $c/session.jsonl; echo "$edit_doc" > $c/session/subagents/agent-1.jsonl
check QUIET "no src edits" $c
# (d) reviewer only inside a subagent transcript does not count
d=$work/d; mkdir -p $d/session/subagents
echo "$edit_src" > $d/session.jsonl; printf '%s\n' "$review" > $d/session/subagents/agent-1.jsonl
check WARN "main edited src, reviewer only in a subagent" $d
# (e) main edited src, no subagents directory
e=$work/e; mkdir -p $e
echo "$edit_src" > $e/session.jsonl
check WARN "main edited src, no subagents dir" $e
# (f) main edited src, reviewed through the scout-review Workflow
f=$work/f; mkdir -p $f
printf '%s\n%s\n' "$edit_src" "$workflow" > $f/session.jsonl
check QUIET "main edited src, scout-review workflow ran" $f
# (g) same as (e) but on the continuation after a block: stop_hook_active passes, no loop
check QUIET "main edited src, stop_hook_active set" $e ',"stop_hook_active":true'
[ $fail = 0 ] && echo "ALL CASES PASS"
exit $fail

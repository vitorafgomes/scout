---
name: stryker-project
description: Mutation-test one Scout project with Stryker.NET and report the surviving mutants that matter. Use when asked to "mutation test", "check whether the tests would catch", "harden the tests around" a project, or after a change to a tool the LLM can invoke, a plugin operation, credential handling or a fail-closed branch. Not a pipeline gate; on demand only. Gate 5's targeted mutation per requirement stays the gate.
disable-model-invocation: true
argument-hint: <Project> [folder-or-class filter]
---

# Mutation testing on a project

Coverage says a line ran. A surviving mutant says the tests would not notice if that line were
wrong. Gate 5 of `scout-pipeline` asks that question by hand, one mutation per requirement; this
skill asks it for a whole project at once, after the fact, and is the way to find the guard nobody
mapped.

## Run

Stryker.NET is a dotnet tool; install once with `dotnet tool install -g dotnet-stryker` (not
installed on the dev machine as of 2026-10-21). Then, from the project's test project, so the run
stays in minutes:

```
cd tests/$0.Tests
dotnet stryker --project "$0.csproj" --reporter json --reporter progress --threshold-break 0
```

`$0` is the project name with its `Scout.` prefix, e.g. `Scout.Llm`. For a narrower run pass a
folder or class as the second argument and use it in `--mutate` (`--mutate "**/Providers/*.cs"`).
The JSON report lands under `StrykerOutput/<timestamp>/reports/mutation-report.json`.

## Read the report, then report

Read the JSON, not the HTML. For each mutated file, list the mutants with status `Survived` or
`NoCoverage`. Ignore `Timeout` and `CompileError`. Then:

1. Group survivors by class and by mutator (`ConditionalExpression`, `EqualityOperator`,
   `BooleanLiteral` on a guard, `StatementRemoval` on a guard or a check).
2. A survivor on a **read-only guard, a tool or plugin allow-list, credential handling, a
   fail-closed branch or a provider selection** is a finding: the suite would not catch the bug it
   represents. Cite `file:line`, the original and the mutated expression, and the invariant page it
   threatens (`vault/pages/invariants/no-executive-writes.md`,
   `vault/pages/invariants/secrets-never-in-source.md`,
   `vault/pages/invariants/pluggable-boundaries.md`).
3. A survivor on logging, message text or a DTO mapping is noise; list the count only.
4. Output: the findings as a table (`file:line | mutator | original -> mutant | invariant`), then
   the noise count, then the mutation score for the scoped folder.

Do not write the missing tests in the same turn unless asked; the report is the deliverable and a
test-writing pass goes through the pipeline's bugfix lane with the survivor as the red test.

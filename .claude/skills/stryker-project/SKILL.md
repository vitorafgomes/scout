---
name: stryker-project
description: Use when asked to mutation-test a Scout project, to check whether its tests would catch a bug, or to harden the tests around it; and after a change to a function the model can invoke, a plugin operation, credential handling or a branch that must fail closed.
disable-model-invocation: true
argument-hint: <Project> [folder-or-class filter]
---

# Mutation testing on a project

Coverage says a line ran. A surviving mutant says the tests would not notice if that line were
wrong. The pipeline asks that question by hand, one mutation per requirement. This skill asks it
for a whole project at once, and finds the guard nobody put in a requirement table. It runs on
request and is never a required step.

## Run

Stryker.NET is a local dotnet tool pinned in `.config/dotnet-tools.json`. It builds and runs the
project's tests many times, so it runs in the container, from the project's test folder:

```
tools/checks/in-docker.sh dotnet tool restore
cd tests/$0.Tests
../../tools/checks/in-docker.sh dotnet tool run dotnet-stryker --project "$0.csproj" --reporter json --reporter progress --break-at 0
```

`$0` is the project name with its `Scout.` prefix, for example `Scout.Llm`. For a narrower run,
pass a folder or a class as the second argument and use it in `--mutate`
(`--mutate "**/Providers/*.cs"`). The report is written to
`StrykerOutput/<timestamp>/reports/mutation-report.json`.

## Read the report

Read the JSON. For each mutated file, take the mutants with status `Survived` or `NoCoverage`,
and group them by class and by mutator (`ConditionalExpression`, `EqualityOperator`,
`BooleanLiteral`, `StatementRemoval`).

| A survivor on | Is |
|---|---|
| The write gate or the read-only guard, a list of functions a turn may call, credential handling, a branch that must fail closed, provider selection | A finding: the tests would not catch the bug it stands for |
| Logging, message text, a mapping between two shapes | Noise: count it |
| Status `Timeout` or `CompileError` | Not a survivor: leave it out |

## Report

Fill in every field. A field with nothing to say holds the word `none`.

```text
FINDINGS
<file:line> | <mutator> | <original> -> <mutant> | <the rule it threatens: a line of .claude/rules/<project>.md or a page under context/pages/invariants/>

NOISE
<count> survivors on logging, text or mapping

SCORE
<mutation score> for <the folder or class that was mutated>
```

The report is the result. Writing the missing tests is a separate task on the pipeline's bug-fix
lane, with the survivor as the failing test.

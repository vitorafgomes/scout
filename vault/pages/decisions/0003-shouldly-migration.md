---
title: "ADR-003 - Shouldly over FluentAssertions"
version: "1.0"
date: 2026-04-21
updated: 2026-10-21
changes: "Migrated from the Obsidian notes on 2026-10-21"
page_type: adr
status: accepted
description: "Decision to migrate test assertions from FluentAssertions to Shouldly after FluentAssertions moved to a commercial license."
source:
  - "obsidian-vault/scout/06-decisions/2026-04-21-shouldly-migration.md"
reliability: medium
---

# ADR-003 - Shouldly over FluentAssertions

## Status

Accepted — 2026-04-21

## Context

The first five test projects (Core, Ollama, Discord, CloudStatus, CodeQA)
all started on **FluentAssertions 6.12.1** — the last permissive release
before the library flipped to a commercial license in v8 (January 2025).
Pinning v6 defers the problem but does not solve it: any future upgrade
walks into a licensing cliff, and mixing two assertion libraries once
anyone adds a new test project is a latent code smell.

The user set the rule: "never use FluentAssertions, we should use
Shouldly".

## Decision

**Every test in every project owned by the user uses Shouldly.**
FluentAssertions is forbidden — new code uses Shouldly from the first
line, and any FluentAssertions code found on sight gets migrated in a
dedicated chore PR.

Concrete rules:

- `Directory.Packages.props` lists `Shouldly 4.3.0` and does **not** list
  `FluentAssertions`.
- Every test `csproj` references `Shouldly`.
- Every `tests/.../Usings.cs` carries `global using Shouldly;` — never
  `global using FluentAssertions;`.
- Migration is a dedicated PR (`chore/tests-shouldly-migration`),
  never mixed with feature work.

## Rationale

- **License.** Shouldly is MIT. FluentAssertions v8+ is commercial. The
  cheapest way to avoid a future license debate is to never allow it in.
- **Consistency.** One assertion library per codebase. Two frameworks in
  the same test suite is immediate cognitive overhead and a code-review
  tax for every PR touching tests.
- **API parity is close enough.** Every assertion FluentAssertions v6
  expressed has a Shouldly equivalent (see the pattern-mapping table in
  the migration PR). A few fluent chains break into two asserts; no
  expressivity is lost.

## Consequences

- PR #22 applies the migration — 37 files, 206 assertions rewritten,
  zero behavior change.
- The rule is codified in:
  - `obsidian-vault/scout/CLAUDE.md` (project-level rule).
  - Global memory `feedback_shouldly_not_fluentassertions.md`
    (applies to every user repo).
  - A note on the `Shouldly` `PackageVersion` line in
    `Directory.Packages.props` explaining why FluentAssertions was
    removed.
- Shouldly refuses null actuals on `ShouldContain` over `string?`.
  Tests asserting on nullable messages (e.g. `StatusReport.Message`)
  use the null-forgiving operator on the asserted side.
- `ShouldThrowAsync<T>()` returns `Task<T>`. Code that needs to inspect
  the thrown exception's fields has to capture the awaited result:
  `var ex = await act.ShouldThrowAsync<T>(); ex.Message.ShouldContain(...)`.

## Alternatives considered

- **Stay on FluentAssertions v6 forever** — rejected. The pin defers
  the problem but does not solve it. A random contributor pulling
  `Directory.Packages.props` into a future project would happily upgrade
  to v8.
- **NUnit / xUnit built-ins (`Assert.Equal`, etc.)** — rejected. Too
  verbose for collection and exception cases; Shouldly's fluent-by-value
  style is significantly easier to read.
- **AwesomeAssertions (FluentAssertions fork)** — rejected. Forks of
  newly-commercial libraries have short lifespans; Shouldly has a stable
  track record and broad adoption in .NET OSS.

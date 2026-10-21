# AGENTS.md - .NET skills & agents routing (Scout)

Referenced from `CLAUDE.md`. This file maps **tasks to the official [`dotnet/skills`](https://github.com/dotnet/skills) plugins** (marketplace `dotnet-agent-skills`, .NET Team at Microsoft). It does **not** change the vault-first workflow: the vault is the source of truth for *what this codebase is and why*; these skills are the source of truth for *how to do a .NET task well*.

> **Verified 2026-10-21 against `~/.claude/plugins/installed_plugins.json`.** Installed for this user: **`dotnet`**, **`dotnet-test`**, **`dotnet-diag`**, plus `dotnet-blazor` and `dotnet-maui` (not applicable here). **`dotnet-data`, `dotnet-aspnetcore`, `dotnet-ai`, `dotnet-msbuild` are not installed.** Every row below is tagged **[installed]** or **[needs install]**; a `[needs install]` skill cannot be invoked until someone runs `/plugin install <plugin>@dotnet-agent-skills`, and reasoning from pretraining because "the table said so" is exactly the failure Rule D exists for. Re-check with `jq -r '.plugins | keys[] | select(test("dotnet"))' ~/.claude/plugins/installed_plugins.json`.

## Order of operations

1. **Vault first.** `vault/index.md`, then `vault/pages/services/<project>.md`, then the linked `vault/pages/invariants/*`, `decisions/*`, `conventions/*`. A page that says "planned" is evidence about intent, not about what runs.
2. **Then a skill/agent from the table below** for the .NET mechanics; invoke by name, do not reason from pretraining when a skill covers the task.
3. **Then grep source** only if vault + skill leave a gap.
4. **Crystallize** new findings back with `/wiki-crystallize`.

This file is consulted from inside the `scout-pipeline` skill: it is what "scans (diff-gated)" in each lane resolves against. A scan whose surface the diff does not touch does not run. How tests prove the requirement is gate 5 of `.claude/skills/scout-pipeline/SKILL.md`; the review lenses are gate 6.

## How to invoke

- **Skill**: `Skill` tool with `skill: "<plugin>:<skill>"`, e.g. `dotnet-test:run-tests`.
- **Agent**: `Agent` tool with `subagent_type: "<plugin>:<agent>"`, e.g. `dotnet-test:test-quality-auditor`. Agents are multi-step orchestrators; use them for large sweeps, not single-file edits.
  - **Known breakage:** an agent whose definition ships a lowercase `tools:` list gets no working tools in Claude Code. In the installed set that is every `dotnet-test:code-testing-*` agent. Do not dispatch them; run the equivalent inline skill instead. `dotnet-test:test-quality-auditor` and `dotnet-test:testability-migration` are unaffected.
- **LSP (Roslyn)**: the installed `dotnet` plugin registers a C# language server for `.cs`. Prefer LSP find-refs over blind grep for symbol work. Requires a .NET SDK on PATH; this repo pins `10.0.100` with `rollForward: latestMinor` in `global.json`.

## Tier 1 - Primary (use routinely on this repo)

| Task | Skill / Agent | Notes for Scout |
|---|---|---|
| Navigate/rename/diagnose C# across the solution | `dotnet` **LSP** (auto) **[installed]** | Solution is `Scout.slnx`. |
| Run / filter .NET tests, read pass-fail | `dotnet-test:run-tests` **[installed]** | Respects Rule B. Surface per `scout-pipeline` SKILL.md gate 4: one test project per `src/Scout.X`, widening through `vault/pages/architecture/project-map.md`. Report the skipped count per project. |
| Coverage plateau / where to add tests | `dotnet-test:coverage-analysis`, `dotnet-test:test-gap-analysis` **[installed]** | |
| Test-quality smells / weak asserts / over-mocking | `dotnet-test:test-anti-patterns`, `dotnet-test:test-smell-detection`, `dotnet-test:assertion-quality`, `dotnet-test:grade-tests` **[installed]** | Gate 5 runs the first and third over every mapped test path. |
| Risk score for complex code under test | `dotnet-test:crap-score` **[installed]** | |
| Broad test-suite health audit | Agent `dotnet-test:test-quality-auditor` **[installed]** | |
| Make code testable (`TimeProvider`, `IFileSystem`) | Agent `dotnet-test:testability-migration` **[installed]** | |
| Hot-path perf, allocations, async traps; benchmarks | `dotnet-diag:analyzing-dotnet-performance`, `dotnet-diag:microbenchmarking`, agent `dotnet-diag:optimizing-dotnet-performance` **[installed]** | The `performance` lens: `scout-optimizer` runs `analyzing-dotnet-performance` over the changed files first. |
| Hang / leak / crash triage | `dotnet-diag:dotnet-trace-collect`, `dotnet-diag:dump-collect` **[installed]** | |
| Pin/install a .NET SDK locally | `dotnet:setup-local-sdk` **[installed]** | |
| Mutation-test one project | `stryker-project` (repo skill, on demand) | Needs `dotnet tool install -g dotnet-stryker`. Not a gate. |

## Tier 2 - Situational, needs install

| When | Skill / Agent |
|---|---|
| Build fails opaquely; binlog analysis | `dotnet-msbuild:binlog-generation`, `dotnet-msbuild:binlog-failure-analysis` **[needs install]** |
| OpenTelemetry wiring | `dotnet-aspnetcore:configuring-opentelemetry-dotnet` **[needs install]** |
| AI/LLM/RAG features in .NET | `dotnet-ai:*` **[needs install]**; Semantic Kernel stays the one LLM path (ADR 0012) whatever a skill suggests |
| Future .NET upgrade, AOT check | `dotnet-upgrade:*` **[needs install]** |

## Not applicable to this repo

- `dotnet-nuget:convert-to-cpm`: Central Package Management is already in place (`Directory.Packages.props`).
- `dotnet-blazor:*`, `dotnet-maui:*`: the UI is Electron + Angular under `ui/`, driven by Bun.
- `dotnet-data:*`: no EF Core or database in the current cut.

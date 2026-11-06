---
title: "Tooling"
version: "1.1"
date: 2026-10-21
updated: 2026-11-06
changes: "v1.1 (2026-11-06): the quality gates ported from quotai: the gated analyzer rules in .editorconfig, AnalysisMode=Recommended, tests/Scout.ArchTests, self-tests for every hook, test-per-project.sh, the diagnostics gate and per-project tests in CI, gate 0 in the pipeline skill. v1.0 founded with the harness."
page_type: convention
status: active
description: "Who runs on which model and writes what, what lives in .claude/ and scripts/, what each hook does, and how the review workflow and the diagnostics script fit the gates."
source:
  - ".claude/"
  - "scripts/"
  - ".github/workflows/ci.yml"
reliability: high
---

# Tooling

The harness was brought over from the owner's `Smart.Management` repo on 2026-10-21 in that repo's shape (sh + jq hooks with case-matrix self-tests, one pipeline skill, a review workflow, an in-repo vault). What was not brought: Jev (the TypeSafe probability gate), the integration-database probe (no database here), the comment sweeper (this repo keeps `<summary>` documentation). Root `CLAUDE.md` names this page; the `scout-pipeline` skill dispatches by it.

## Model routing (owner, 2026-10-21)

The session model does every survey, plan and check; Opus implements and reviews; Sonnet does the simple work; Haiku runs the gates.

| Who | Model, effort | Writes code? |
|---|---|---|
| **The manager** (the session thread): classifies, reads the vault, states the six-part plan, dispatches, checks the result against the plan line by line, owns `vault/index.md`, `vault/log.md`, the conventions and the process ADRs. | session model | Vault, docs, `.claude/**`, fast-path one-liners only. |
| **`scout-implementer`**: implements a non-trivial change from the plan (adapters, providers, plugins, skills, control-plane commands, knowledge indexing, hosts, tests), Rule B per edit, and proves gate 5 itself: a targeted mutation per `new behaviour` entry with the failure quoted, the file restored from a backup copy and rebuilt; behaviour locks green before and after. | opus, high | **Yes**: `src/**`, `tests/**`, `ui/**` of the named project(s), plus the vault pages the plan names. Never git history. |
| Any simple, mechanical, well-specified task (`general-purpose` on `model: sonnet`). | sonnet, medium | **Yes**, the named files only. |
| **`scout-mechanic`**: runs build, format, diagnostics and test commands exactly as dispatched, reports per project with skipped counts, never fixes. | haiku, low | No. |
| **`scout-reviewer`**: gate 6, dispatched by the `scout-review` workflow one instance per lens (`read-only`, `secrets`, `boundaries`, `invariants`); checks the gate 5 evidence and the test-quality results the dispatch carries; each finding then faces a refute pass. | opus, high (refute pass: sonnet, high) | No. |
| **`scout-optimizer`**: the `performance` lens, mandatory on a diff touching streaming, knowledge indexing or retrieval, a hosted service, the gRPC control plane or an LLM provider call path. | opus, high | No. |
| Review gate, generic lens (`/code-review`) | Built-in skill, `medium`, never `--fix`; only when the diff touches `Scout.ControlPlane.Contracts`, a host, or the owner asks. | No. |
| Surveys past a handful of targeted reads | `Explore` subagent. | No. |

## `.claude/skills/scout-pipeline`

Since 2026-11-06 the skill opens with gate 0 (`.claude/skills/scout-pipeline/SKILL.md:17`): classification starts from `git status --porcelain` and `git diff HEAD --stat`, written as an instruction and never as a dynamic shell block, because a skill that shells out while loading fails to load wherever Bash is denied.

The single source of the pipeline: classification (fast path or full), the never-list, the ladder (`ponytail` after the vault, before the edit), the six-part plan, the nine gates, the two dispatch templates. Lane files `feature.md`, `bugfix.md`, `refactor.md`, `diagnose.md` carry only the stage order. `evals/` holds four `claude plugin eval` cases (a provider bug is the bugfix lane, a doc typo is the fast path, a one-line outbound call is never the fast path and names the `read-only` lens, a Contracts edit resolves to every test project and the ui surface); a SKILL.md edit that changes what a grader asserts updates the eval in the same diff.

## `.claude/workflows/scout-review.js`

Gate 6 as a `Workflow` script: `Workflow({ name: "scout-review", args: { module: "<Scout.Project>", lenses, dispatch, range? } })`. `invariants` always runs and also owns regression coverage, Rules H and J and generic correctness; the other lenses come from the gate 6 table, each on its own `scout-reviewer` (opus). Every finding then goes to an independent `scout-reviewer` on sonnet whose only job is to refute it; a finding survives only when the refuter reads the cited line and the cited page and cannot. The result is `{ confirmed, refuted, checked, uncited }`. Both agents read `git diff HEAD` plus `git status --porcelain`. The `dispatch` arg is the reviewer template from the skill, filled in; the script builds neither the map nor the evidence. Without the Workflow tool the fallback is the direct `scout-reviewer` dispatch.

## `.claude/skills/stryker-project`

On-demand mutation testing of one project with Stryker.NET (`dotnet tool install -g dotnet-stryker`, not installed as of 2026-10-21), `disable-model-invocation: true` so only the owner starts it. Survivors on a read-only guard, a tool or plugin allow-list, credential handling, a fail-closed branch or a provider selection are findings with `file:line` and the invariant page; logging, text and mapping survivors are a count. Not a gate: gate 5's targeted mutation per requirement stays the proof.

## `.claude/skills/wiki-*`

The five wiki skills (`wiki-crystallize`, `wiki-ingest`, `wiki-integrate`, `wiki-lint`, `wiki-query`) and their shared references, copied from the Smart.Management repo. They read `vault/wiki-config.md` and `vault/wiki-schema.md`; the operation contract is `vault/CLAUDE.md`.

## `.claude/hooks/`

All seven fail open: a missing `jq`, malformed JSON, or any error exits 0 having written nothing, so a broken hook can never block work. That is why the first one exists.

| Hook | Event | What it does |
|---|---|---|
| `session-start.sh` | SessionStart | Proves the hook runtime works, loudly: checks `jq` is on PATH and pipes `git commit -m x` through `pre-git-guard.sh`; when either fails it emits a `systemMessage` saying the hooks are dead and which gates are silent. |
| `prompt-router.sh` | UserPromptSubmit | One line reminding that a code task routes through `scout-pipeline` first and that a new outbound call is a review lens, never a fast path. |
| `pre-git-guard.sh` | PreToolUse (Bash) | Turns Rule E into a permission **ask**: history-writing git, `gh pr create/merge`, `docker push`, `wrangler deploy`, `dotnet ef database update`. "Ask" not "deny": "commit this for me" still works, it cannot happen silently. Read-only git passes. |
| `post-edit-rule-b.sh` | PostToolUse (Edit\|Write) | On `src/**/*.cs`: the edit needs its test now, the gates are build + diagnostics + ArchTests + whitespace format, and Rule A for any new outbound call. On `ui/packages/**/*.ts` (not a spec): a spec now, then typecheck, lint, format, test and build in `ui/`. |
| `post-edit-oracle-note.sh` | PostToolUse (Edit\|Write) | On `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `global.json` and `.github/workflows/**`: the build strictness grades a change and is not part of it; the reply must say why the task needed the edit. |
| `stop-review-gate.sh` | Stop | **Blocks** the turn once when the session edited `src/**/*.cs` without dispatching `scout-reviewer` (an `Agent` call or a `Workflow` run of `scout-review`). `stop_hook_active` passes on the continuation, so no loop. Keyed on what *this* session edited, in `<session>.jsonl` and every `<session>/subagents/*.jsonl`, not on working-tree dirtiness. |
| `session-end-metrics.sh` | SessionEnd | Appends one JSON line per session to `.claude/metrics/sessions.jsonl` (gitignored): src and test edits, reviewer dispatches, review workflow runs, pipeline invocations, agents, tool calls, `endedUnreviewed`. |

All seven hooks carry a case-matrix self-test (`<hook>.test.sh`, prints `ALL CASES PASS`; `post-edit-rule-b` and `prompt-router` got theirs on 2026-11-06); CI runs all seven plus `scripts/check-vault-refs.test.sh`, `scripts/test-per-project.test.sh` and a real `check-vault-refs.sh` run in its first step, before .NET is set up (`.github/workflows/ci.yml:17`).

## The gated rules in `.editorconfig`

Ported from quotai (itself from WayUp_Backend) on 2026-11-06. `Directory.Build.props` sets `AnalysisMode=Recommended` (`Directory.Build.props:9`) on top of `TreatWarningsAsErrors` and `EnforceCodeStyleInBuild`, and the root `.editorconfig` raises fifteen rules from their shipped suggestion or info severity to `warning` under `[*.cs]` (`.editorconfig:43`): IDE0005, IDE0042, IDE0051, IDE0052, IDE0059, IDE0060, IDE0130, IDE0270, IDE0290, CA1305, CA1826, CA1859, CA1865, CA2016, SYSLIB1045 (quotai gates CA1873 as well; here it is triaged off with CA1848, below). A rule left at its shipped severity never reaches the CLI, so the diagnostics gate would pass without seeing it. `tests/Scout.ArchTests/AnalyzerGateTests.cs:13` lists the same fifteen and fails when the root config lowers one, a nested `.editorconfig` opts out, a `.csproj` names one in a `NoWarn` (the one exception is `Scout.ControlPlane.Contracts`, whose IDE0005 fires in the C# Grpc.Tools generates from `worker.proto`; `:112`), or a bare `#pragma` silences one without a reason on the same line.

Triaged off, each with its reason on the line above it (`.editorconfig:73`): CA1848 and CA1873 (LoggerMessage source generation and its companion; every structured log call boxes an int or a bool, 243 + 76 sites, and nothing here logs on a hot path), CA1711 (reserved suffixes are domain names), CA1716 (`Scout.Operator` is the feature's name), and CA1707 under `[tests/**.cs]` only (`:84`; test names read as sentences). A rule leaves that block in the change that fixes its sites. One `#pragma` carries IDE0051 with its reason: a record's `PrintMembers`, which the synthesized `ToString` calls and the analyzer does not see (the fixer deleted it on 2026-11-06 and two tests caught it).

Not adopted from quotai: the negation-style scanner (`!x` written as `x is false`). Scout has about 1,200 negations written the C# way, and the vault's conventions never asked for the other form; it is a style decision for the owner, not a gap in the gates.

## `scripts/check-diagnostics.sh`

`scripts/check-diagnostics.sh <csproj|slnx|-> [info|warning|error]`. The build already enforces code style, but test projects generate no documentation file and without it IDE0005 (unused usings) is never emitted (dotnet/roslyn#41640); info-level diagnostics never fail a build either. The script forces both on, rebuilds the target at detailed verbosity with `MSBUILDDISABLENODEREUSE=1`, and fails on any `CS/CA/IDE/SA/MSB` diagnostic at or above the minimum severity (default `info`). A third argument picks the configuration (default Debug). Pass one `.csproj` locally; CI runs it on `Scout.slnx` in Release as the build itself, once, and the tests run on its output (owner decision 2026-11-06; `.github/workflows/ci.yml:55`), so a diagnostic the agent gate missed still fails the PR.

## `scripts/test-per-project.sh`

`scripts/test-per-project.sh [dotnet test args]` runs every `tests/**/*.csproj` one at a time and fails when a project fails, skips a test, runs nothing, or prints a summary line it cannot parse; a solution-wide `dotnet test` reports one green total, which has hidden a project that did not compile and a suite that was skipped entirely. It is the pipeline's gate 4 ("`N passed` per project, Skipped 0") in one command, and the CI test step since 2026-11-06 (`.github/workflows/ci.yml:60`). `scripts/test-per-project.test.sh` runs it against a fake `dotnet` for the six verdicts.

## `dotnet format` on this machine

`dotnet format` under the pinned SDK (10.0.1xx) fails to open the MSBuild workspace (the build-host pipe bug), so Rule B's `dotnet format Scout.slnx --verify-no-changes` runs under the 10.0.2xx band: `DOTNET_ROOT=$HOME/.dotnet $HOME/.dotnet/dotnet format Scout.slnx --verify-no-changes` (SDK 10.0.201 is installed there), and CI installs 10.0.2xx for the same step (`.github/workflows/ci.yml:47`). `dotnet format whitespace --folder <dir>` is the fallback under the pinned SDK; it checks whitespace only, not using order or style. The same SDK's `format style` and `format analyzers` applied the IDE and CA fixers across the solution on 2026-11-06. Two of its edits were wrong and the tests caught them: IDE0051 deleted a record's `PrintMembers`, and IDE0005 was re-introduced in a file an agent reverted; the fixers are a first pass, the build and the tests are the gate.

## `scripts/check-vault-refs.sh`

Resolves every `path:line` citation in `vault/pages` (paths under `src`, `tests`, `ui`, `scripts`, `.claude`, `.github`) against the working tree; fails on a missing file or a line past the end. It proves the line exists, never that it still describes the claim; that part is read. Pages whose `status` marks them as a record are skipped. Runs in gate 7b and in CI.

## `graphify-out/graph.json` and the git hooks that refresh it

An AST knowledge graph of the repo (graphify 0.9.73), built locally and gitignored, never committed. `scripts/install-git-hooks.sh` copies the three versioned hooks from `scripts/git-hooks/` (`post-commit`, `post-checkout`, `post-merge`) into the clone's common hooks directory, so every worktree refreshes the graph after a commit, a checkout or a pull; `post-merge` is hand-written because a fast-forward pull fires neither of the other two, and it depends on the `GRAPHIFY_DIFF_BASE` line in `post-commit`, which `graphify hook install` would rewrite, so re-apply it after upgrading graphify. `.graphifyignore` excludes `src/old/` and `tests/old/` (the archived first cut would otherwise duplicate every live label, and `graphify affected` needs a unique one). First build 2026-10-21: 6655 nodes, 13988 edges, AST only. Manual refresh: `graphify update . --no-cluster`. The querying rules (which commands, what never to run) live once, in the owner's global `~/CLAUDE.md`; in this pipeline `graphify affected "<Symbol>" --depth 2` gives the starting Files list of the plan and `god-nodes --top 10` says whether the change lands on a hub.

## Permissions

`.claude/settings.json` allows without a prompt the read-only commands the gates need: `dotnet build|test|format`, read-only git, the two scripts and the six self-tests. Everything Rule E covers goes through `pre-git-guard.sh`.

## Related

- [[pages/conventions/testing]]
- [[pages/architecture/project-map]]
- Root `CLAUDE.md`, `AGENTS.md`, `.claude/skills/scout-pipeline/SKILL.md`

---
title: "Tooling"
version: "1.0"
date: 2026-10-21
updated: 2026-10-21
changes: "Founded with the harness: agents, hooks, scripts, review workflow, Stryker skill"
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
| `post-edit-rule-b.sh` | PostToolUse (Edit\|Write) | On `src/**/*.cs` only: the edit needs its test now, the gates are build + format + diagnostics, and Rule A for any new outbound call. |
| `post-edit-oracle-note.sh` | PostToolUse (Edit\|Write) | On `Directory.Build.props`, `Directory.Packages.props`, `.editorconfig`, `global.json` and `.github/workflows/**`: the build strictness grades a change and is not part of it; the reply must say why the task needed the edit. |
| `stop-review-gate.sh` | Stop | **Blocks** the turn once when the session edited `src/**/*.cs` without dispatching `scout-reviewer` (an `Agent` call or a `Workflow` run of `scout-review`). `stop_hook_active` passes on the continuation, so no loop. Keyed on what *this* session edited, in `<session>.jsonl` and every `<session>/subagents/*.jsonl`, not on working-tree dirtiness. |
| `session-end-metrics.sh` | SessionEnd | Appends one JSON line per session to `.claude/metrics/sessions.jsonl` (gitignored): src and test edits, reviewer dispatches, review workflow runs, pipeline invocations, agents, tool calls, `endedUnreviewed`. |

Five hooks carry a case-matrix self-test (`<hook>.test.sh`, prints `ALL CASES PASS`); CI runs all five plus `scripts/check-vault-refs.test.sh` and a real `check-vault-refs.sh` run in its first step, before .NET is set up.

## `scripts/check-diagnostics.sh`

`scripts/check-diagnostics.sh <csproj|slnx|-> [info|warning|error]`. The build already enforces code style, but test projects generate no documentation file and without it IDE0005 (unused usings) is never emitted (dotnet/roslyn#41640); info-level diagnostics never fail a build either. The script forces both on, rebuilds the target at detailed verbosity with `MSBUILDDISABLENODEREUSE=1`, and fails on any `CS/CA/IDE/SA/MSB` diagnostic at or above the minimum severity (default `info`). Pass one `.csproj`; a solution rebuild is slow.

## `scripts/check-vault-refs.sh`

Resolves every `path:line` citation in `vault/pages` (paths under `src`, `tests`, `ui`, `scripts`, `.claude`, `.github`) against the working tree; fails on a missing file or a line past the end. It proves the line exists, never that it still describes the claim; that part is read. Pages whose `status` marks them as a record are skipped. Runs in gate 7b and in CI.

## Permissions

`.claude/settings.json` allows without a prompt the read-only commands the gates need: `dotnet build|test|format`, read-only git, the two scripts and the six self-tests. Everything Rule E covers goes through `pre-git-guard.sh`.

## Related

- [[pages/conventions/testing]]
- [[pages/architecture/project-map]]
- Root `CLAUDE.md`, `AGENTS.md`, `.claude/skills/scout-pipeline/SKILL.md`

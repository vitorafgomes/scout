---
title: "Audit"
version: "1.18"
date: 2026-11-11
changes: "v1.18 (2026-11-13) the checks before a review, first part (a done review at the same commit blocks; the size never past 60 minutes): a review start answers ReviewStart with its checks, size, Docker runs, head, another review and grants, each ok, a warning or a block, kept once on the job's new."
page_type: service
status: active
description: "Runs a whole-repository audit as a job: plans the batches from the repository snapshot, runs one model turn per batch with a closed scope, keeps the plan, the files read and the findings in SQLite under Scout's data folder, posts the findings into the conversation that started it, and resumes after a restart."
source: ["src/Scout.Audit/", "tests/Scout.Audit.Tests/", "tests/Scout.ArchTests/PersistenceTests.cs", "context/pages/decisions/0052-persistence-behind-stores-on-entity-framework.md", "src/Scout.Host.Console/HostComposition.cs", "src/Scout.Host.Console/ReviewEnvironmentAdapter.cs", "tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs", "context/pages/decisions/0042-audit-as-a-job-with-saved-progress.md"]
reliability: high
---

# Audit

> An audit of a whole repository is a job Scout drives batch by batch, so it covers every area without the owner saying "continue" and the model never has to remember the last batch.

## Responsibility

Owns the audit job: the plan of batches, the `audit.db` file, its three tables and their migrations, the batch turn's instructions and the functions it offers, the merge of findings across batches, the posts into the originating conversation, and the start, pause, stop and status sentences. Does not own the snapshot or the reads (the GitHub plugin, [[pages/services/plugin-github]]), the project search ([[pages/services/knowledge]]), the audit text itself (`code-audit`, [[pages/services/skills]]), the cards and the issues asked from them (the desktop and `create_issue`, each issue queued for the owner's approval since 2026-11-09, [[pages/decisions/0032-per-feature-write-permissions]]), nor the chat functions that call it (the GitHub plugin declares them; see Open items).

## Rules

The rules this project must keep, and what to read before changing it, are in `.claude/rules/audit.md`. Claude Code loads that file when a task touches the project.

## Topics

Read the page your change touches, not all of them.

| Page | Read when a change touches | Characters |
|---|---|---|
| [[pages/services/audit/public-contract\|Public contract]] | Public contract | 5897 |
| [[pages/services/audit/how-it-works-mermaid\|How it works: mermaid]] | mermaid; Start; Review start; Checks before a review | 11293 |
| [[pages/services/audit/how-it-works-plan\|How it works: Plan]] | Plan; Rows; Merge | 7984 |
| [[pages/services/audit/how-it-works-batch-turn\|How it works: Batch turn]] | Batch turn; Worker | 11154 |
| [[pages/services/audit/how-it-works-pause\|How it works: Pause]] | Pause; Continue from Scout Desktop; Stop and status | 4625 |
| [[pages/services/audit/invariants-it-preserves\|Invariants it preserves]] | Invariants it preserves | 2759 |
| [[pages/services/audit/tests\|Tests]] | Tests | 9456 |
| [[pages/services/audit/open-items\|Open items]] | Open items | 2425 |

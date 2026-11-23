---
title: "ADR 0073 - The numbers of each model turn are kept, and the turns' content is recorded only on request"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Every model turn of a job writes one row of numbers and names to the job database, always; the card's counts are read from those rows; an opt-in record of each turn's calls, results and answer, off by default, is written as JSON lines under Scout's own data folder; both stay inside Scout's folders and neither failing fails a turn."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/SqliteAuditStore.cs
  - src/Scout.Audit/AuditBatchRunner.cs
  - src/Scout.Audit/AuditOptions.cs
  - src/Scout.Audit/AuditServiceCollectionExtensions.cs
  - src/Scout.Audit/Storage/Migrations/20261122120000_AuditTurns.cs
reliability: high
---

# ADR 0073 - The numbers of each model turn are kept, and the turns' content is recorded only on request

> Every turn leaves its numbers in the job's database, so runs can be compared and the card's counts survive a restart; what a turn read and answered is written only when the owner turns the record on, and never outside Scout's own folder.

## Status

Accepted on 2026-11-21, built at the owner's request to measure the review and to stop the card depending on counts held in memory.

## The problem

A review's cost and behaviour could not be compared from one run to the next: nothing kept how many turns ran, how long they took or what they presented, and the card's count of model turns was held in memory, lost when the worker restarted. Diagnosing a wrong review needed the turns' calls and answers, which nothing kept.

## The decision

- **The table `audit_turn`** (migration `20261122120000_AuditTurns`, `src/Scout.Audit/Storage/Migrations/20261122120000_AuditTurns.cs:26`). One row per model turn of a review or an audit: the job, the batch's order (null for the second reading), the reading (general, removed, requests or contest), the second reading's group, the attempt, whether it was the extra evidence request, its start and elapsed milliseconds, the request's length, how many times each function was called (a JSON object of names to counts), the findings presented, placed and left out, the failure sentence, and for the second reading how many held, were refuted and stayed uncertain. No answer text, no file content, no argument. A database from before the table opens and is brought up to date.
- **Always on.** The runner writes a row after each attempt (`src/Scout.Audit/AuditBatchRunner.cs:807`), the worker one per second-reading group. A row that cannot be written is logged and never fails the turn.
- **The card's counts come from the rows** (`SqliteAuditStore.TurnTotalsAsync`, `src/Scout.Audit/SqliteAuditStore.cs:156`): "Scout asked the model N times for this review." and the focused readings not completed. They survive a restart of the worker in the middle of a job.
- **The run's names.** The job row keeps the Scout version and the provider and chat model names that last ran it, `provider/model`, written when a worker starts or resumes the job (`:173`); names only, never a key.
- **The opt-in record.** With `Scout:Audit:RecordTurns` on (`src/Scout.Audit/AuditOptions.cs:18`; `Scout__Audit__RecordTurns=true`, which the desktop passes on to the worker it starts), each review turn appends a line to `audit/<job>/batch-<order>-<reading>.jsonl` (each attempt a line, the extra request included) and each second-reading group to `contest-<group>.jsonl`: each call's name and arguments, each result's length and first 500 characters, the answer cut at 20,000 characters, the findings presented, placed and left out with why, and the elapsed time (`AuditBatchRunner.cs:845`). The folder is `audit` under Scout's own data folder whatever the database path says (`src/Scout.Audit/AuditServiceCollectionExtensions.cs:94`). A record that cannot be written is logged and never fails the turn.
- **Matching a row and a record.** A batch row (job, batch, reading, attempt) is the line of that attempt in `batch-<batch>-<reading>.jsonl`; a second-reading row (job, `contest`, group) is the line of `contest-<group>.jsonl`.

## Calls that leave the process

None. Both are writes inside Scout's own data folder, Scout's own state under [[pages/invariants/no-executive-writes]].

## What follows

- **Gained:** each run's cost and behaviour can be compared; the card's counts are true after a restart.
- **Paid:** a row per turn in the database.
- **To watch:** the record holds the start of each file a turn read; it is off by default and stays on this computer.

## Rejected options

- **Keep the counts in memory:** lost on a restart.
- **Keep the turns' content always:** it holds repository code; numbers are enough for comparing runs.

## How it is held

- `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:1806` (one row per turn, the extra request and the second reading included, no text), `:1829` (the counts survive a restart), `:1845` (a row that cannot be written), `:1858` (version and model names).
- `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:650` (an older database opens and keeps turns).
- `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:1323` (the record off and on), `:1351` (always in Scout's own folder), `:1360` (a record that cannot be written); `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:1682` (the second reading's calls in its record).

## Links

- Projects it shapes: [[pages/services/audit]]
- Invariants it keeps: [[pages/invariants/no-executive-writes]]

## Amendments

None yet.

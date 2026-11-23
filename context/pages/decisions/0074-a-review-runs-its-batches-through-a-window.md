---
title: "ADR 0074 - A review runs its batches through a window, and a review batch turn has its own bound"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A review keeps at most its parallel count of batches in flight and starts the next pending one as soon as one ends, so a batch that hangs holds one place, never the others; a review batch turn has 3 minutes, not the 10 audits and the second reading keep; each turn's row says when its last function call ended."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/AuditWorker.Reviews.cs
  - src/Scout.Audit/AuditOptions.cs
  - src/Scout.Audit/AuditBatchRunner.cs
reliability: high
---

# ADR 0074 - A review runs its batches through a window, and a review batch turn has its own bound

> One slow call holds one place of a review, for minutes, never the whole review for ten.

## Status

Accepted on 2026-11-21, by the owner's rule that a review asked for always ends in a valid result, never a stall. It amends [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]], whose text stays as decided: the waves it describes become the window below.

## The problem

Seen live: a review stopped advancing for almost nine minutes. One turn of a wave hung to near its 10-minute bound, and the wave's other batches, finished long before, waited for it, since the run awaited every batch of a wave before starting the next.

## The decision

- **The window** (`src/Scout.Audit/AuditWorker.Reviews.cs:326`). At most `Scout:Audit:ReviewParallelBatches` batches are in flight; when one ends, the next pending batch in the plan's order starts. Before each start (`:385`) the job must still be running and its running time under the 60-minute bound, and, each time as many batches as the window holds have started, its head must still be the commit reviewed; otherwise no batch starts, the ones in flight end, and the run says why as before. A batch is handed the titles of the findings saved when it starts. A provider limit stops new starts; the job pauses once, with one notice, after the ones in flight are saved. A stop or a pause by the owner is seen before the next start. A resumed job runs its pending batches the same way.
- **The review batch turn's bound** (`src/Scout.Audit/AuditOptions.cs:40`, `Scout:Audit:ReviewTurnTimeout`, 3 minutes). Review batch turns measured on real pull requests took 8 to 90 seconds; 3 minutes is twice the slowest with room. A turn past it is retried once and then the batch says "the AI model did not answer in time". Audits and the second reading's groups keep `Scout:Audit:TurnTimeout`, 10 minutes, since they read more.
- **What a hang leaves in the record.** Each turn's row keeps `last_call_ms`, when its last function call ended (`src/Scout.Audit/AuditBatchRunner.cs:840`, migration `20261123120000_TurnLastCall`): a turn whose elapsed time is far past it waited on the model after its reads; a turn with no call or a late last call waited on a read.

## Why it hung

Read from the code, a chat turn and a review batch turn share the chat completion service and its HTTP client, which sets no limit on connections to one server; the snapshot store, whose reads and extraction take no lock; and the kernel, whose filters are stateless. The locks found are short and never held across an await. Nothing in the code shows one turn waiting on the other, so the cause is not known; the new `last_call_ms` tells, on the next hang, whether the wait was on the model or on a read.

## Calls that leave the process

None added. The window starts the same batch turns; it reads the head as often as the waves did.

## What follows

- **Gained:** a hung turn costs one place for at most twice 3 minutes.
- **Paid:** a turn slower than 3 minutes is cut and retried.
- **To watch:** a provider that is slow for every turn now fails batches where it once only slowed them.

## Rejected options

- **Keep the waves with a shorter bound:** a wave would still wait for its slowest turn.

## How it is held

- `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:832` (a hung batch while the others start and end, in the plan's order).
- `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:1064` (a turn past its bound is retried once and reported within seconds).
- The window's pause and stop with batches in flight: the existing tests of a provider limit and of a stop, now gated so three batches are in flight.

## Links

- Projects it shapes: [[pages/services/audit]]
- Decisions it amends: [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]]

## Amendments

None yet.

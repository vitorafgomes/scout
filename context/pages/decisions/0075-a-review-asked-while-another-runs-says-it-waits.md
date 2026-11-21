---
title: "ADR 0075 - A review asked while another runs says it waits, and reviews run in the order they joined the queue"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A running review that is not the one the worker runs waits, read in one place from the order of the running jobs; it says so once in its conversation and in the bell, can be stopped while it waits, and turns running when the worker picks it. The queue's order is when each review was created or last continued."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/RepositoryAudits.Reviews.cs
  - src/Scout.Audit/SqliteAuditStore.cs
  - src/Scout.Audit/AuditWorker.cs
  - src/Scout.Audit/AuditWorker.Reviews.cs
  - ui/packages/main/src/notifications/sources.ts
reliability: high
---

# ADR 0075 - A review asked while another runs says it waits, and reviews run in the order they joined the queue

> A review that waits says so at once, and for which review.

## Status

Accepted on 2026-11-21, by the owner's rule that a review asked for always ends in a valid result. It amends [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]], whose worker runs one review at a time.

## The problem

Seen live: a review asked by a mention while another review ran was created as running, with its batches pending, and nothing said it waited. The mention looked ignored until the first review ended.

## The decision

- **The waiting state is derived, in one place** (`RepositoryAudits.Ahead`, `src/Scout.Audit/RepositoryAudits.Reviews.cs:468`). A running review waits when another running review is ahead of it in the worker's queue; the review it waits for is the one directly ahead. No status is added, so no migration, and a database from before opens as it was. Everything that reads the state uses it: the status answer, the Continue answer and the duplicate check of a start say "waiting for the review of owner/repo#N to end".
- **The queue's order is when a review joined it** (`src/Scout.Audit/SqliteAuditStore.cs:82`): when it was created, or when it was last continued after a pause, the earlier first; the worker runs the first. The query still reads by its index and orders in memory. A paused review that is continued goes behind the reviews already waiting, since they were told they start by themselves; with that order the review the worker runs is always the first, so the derived state never names a running review as waiting.
- **One sentence, once** (`WaitingNotice`, `:458`; `PostWaitingAsync`, `:485`): "The review of owner/repo#N waits for the review of owner/repo#M to end; it starts by itself." It is posted to the review's conversation when the review is created behind another, and in place of the resume line when a continued review waits. The bell reads it as a `waiting` entry with the same sentence and Stop (`ui/packages/main/src/notifications/sources.ts:30`).
- **Running when picked** (`src/Scout.Audit/AuditWorker.Reviews.cs:237`). The worker posts the review's progress line, with nothing done yet, when it picks a review that was said to wait, so its entry turns running at once; the waiting sentence is not said again. The reviews said to wait are held in the worker's signal, and noted again when the worker starts (`src/Scout.Audit/AuditWorker.cs:148`), so this holds across a restart.
- **Stopped while waiting** (`OthersLeftAsync`, `src/Scout.Audit/AuditWorker.Reviews.cs:500`). The worker posts the stop of another review as each batch of the running review ends, not after the running review, so a stopped waiting review says it ended while the one ahead still runs.

## Calls that leave the process

None added. The sentence goes through the conversation notifier the worker already posts through.

## What follows

- **Gained:** a review asked while another runs is visible from the moment it is created.
- **Paid:** a continued review loses its place to reviews asked while it was paused.
- **To watch:** a review stopped while it waited is said once a batch of the running review ends, up to the review batch turn's bound later.

## Rejected options

- **A new status value:** a migration, and every reader of "running" to change, for a state the order already holds.
- **Order by creation alone:** a continued older review would be first in the order while the worker still runs the newer one, and the derived state would call the running review waiting.

## How it is held

- `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:880` (a review asked while another runs waits, says so once, and starts by itself).
- `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:909`, `:933` and `:952` (three reviews in order; a continued review behind; a restart with one running and one waiting).
- `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:972` (a waiting review stopped ends while the one ahead runs).
- `ui/packages/main/test/notifications-job-controls.test.ts` and `ui/packages/main/test/mcp-tools.test.ts` (the waiting entry, its Stop, `list_reviews` and `get_review`).

## Links

- Projects it shapes: [[pages/services/audit]], [[pages/services/desktop-ui]]
- Decisions it amends: [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]]

## Amendments

None yet.

---
title: "ADR 0062 - The review job runs to its end, in parallel, shown and controlled from the bell"
version: "1.0"
date: 2026-11-19
updated: 2026-11-19
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A pull request review job reads every batch, code first, several batches at once, shows its progress in the bell of Scout Desktop and nothing of it in the chat until its one card, takes the owner's pause, continue and stop from the bell, and pauses to ask at 60 minutes of running time."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/AuditWorker.Reviews.cs
  - src/Scout.Audit/RepositoryAudits.Reviews.cs
  - src/Scout.Audit/SqliteAuditStore.cs
  - src/Scout.Audit/AuditServiceCollectionExtensions.cs
  - src/Scout.ControlPlane/Controls/OwnerControlService.cs
  - src/Scout.Host.Console/JobControlPoller.cs
  - ui/packages/main/src/notifications/sources.ts
  - ui/packages/main/src/notifications/job-controls.ts
  - ui/packages/renderer/src/app/core/review-findings.ts
reliability: high
---

# ADR 0062 - The review job runs to its end, in parallel, shown and controlled from the bell

> A review job reads every batch, code first, several at once; its progress is in the bell of Scout Desktop and not in the chat, where one card arrives at the end; the owner pauses, continues and stops it from the bell; at 60 minutes of running time it pauses and asks.

## Status

Accepted on 2026-11-19. It amends [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] and [[pages/decisions/0061-the-pull-request-review-job-as-built]], whose texts stay as they were decided. The owner decided on 2026-11-19: the review runs to its last batch, with no bound on batches; code is read before tests, configuration and documentation; several batches run at once, 4, and 1 on the local default provider, by option; the progress is shown in the bell with the findings so far, and nothing of it in the chat, where one card arrives at the end; the owner pauses, continues and stops a review from the bell; at 60 minutes of running time the review pauses and asks, and a continue gives 60 more. The design was drawn on the Main artboard first and approved by the owner before the code ([[pages/conventions/desktop-prototype]]).

## The problem

As built by ADR 0061, a review stopped at its tenth batch or sixtieth minute and listed the files left unread, in folder order, where code could come after documentation. Batches ran one at a time, each line reached the chat, and only the chat could pause or stop a review.

## The decision

This record replaces these sentences:

- ADR 0055: "**Bounded.** 10 batches and 60 minutes; whatever is left, and every file GitHub gave no diff for, is counted as not read." A file with no diff is still kept not read; nothing else is.
- ADR 0061: "**The bounds.** 10 batches, and 60 minutes of running time: the sum of the batches' turns, each from its start to its end as the batch rows keep them, so a paused time never counts and the sum survives a restart."
- ADR 0055: "One progress line per batch" read as a line for every origin. The line goes to the desktop only.
- ADR 0055: "The plan is cut by Scout from the changed files at the head commit, sized by patch length, through the same planner as an audit's". The plan is now cut group by group.

What Scout does:

- **The order of reading.** `RepositoryAudits.ReadingOrder` puts each changed file in one group by its path alone: 0 code, 1 tests, 2 build and configuration, 3 documentation (`src/Scout.Audit/RepositoryAudits.Reviews.cs:257`). The groups are planned in that order, each through the planner (`:112`). A pull request can choose a path's group, but can no longer make a file be dropped: there is no bound on batches. The files GitHub counts but did not list, past its 3,000, are one batch kept not read, "GitHub did not list these files", so the read line counts every changed file and the verdict is never ready to merge (`:133`, `src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:56`).
- **Waves.** The worker takes up to `ReviewParallelBatches` pending batches at once (`src/Scout.Audit/AuditWorker.Reviews.cs:69`), begins them all, and runs their turns together (`:139`). The turns share the pull request read once, the head read before each wave, and the titles of the findings known when the wave started. Each batch is saved as it ends, under one lock, with its progress line (`:152`). A provider limit in any batch pauses the job once, after the others are saved (`:186`).
- **The option.** `Scout:Audit:ReviewParallelBatches`, 1 to 8, 4 unless set (`src/Scout.Audit/AuditOptions.cs:31`). It is 1 when it is not set and `Scout:Llm:Provider` is empty or `Ollama`, so a local model is not sent several turns together (`src/Scout.Audit/AuditServiceCollectionExtensions.cs:35`, `:47`).
- **The lines.** The progress line after each batch and the resumed line go only to the desktop's local chat (`src/Scout.Audit/AuditWorker.cs:213`, `src/Scout.Audit/RepositoryAudits.Reviews.cs:409`). The start answer, a pause that needs the owner, the owner's pause and the card go to the job's conversation.
- **The owner's controls, end to end.**
  - The bell offers Pause and Stop on a running review job, and Continue and Stop on a paused one (`ui/packages/shared/src/ipc-contract.ts:2082`).
  - A click travels on the existing channel `scout:notifications.control` (`ui/packages/main/src/notifications/job-controls.ts:477`), as `{ kind: 'review', owner, repo, number, action }` (`:101`), to the control plane's existing `ControlJob`.
  - The control plane takes the kind `review` (`src/Scout.ControlPlane/Controls/OwnerControl.cs:13`) with its pull request number. It refuses a review without a number above zero, an unknown kind or action, and a bad repository name, and queues nothing (`src/Scout.ControlPlane/Controls/OwnerControlService.cs:222`).
  - The worker gate hands the control to `JobControlPoller`, which passes it to `IPullRequestReviews.ControlAsync` and settles it once with the sentence (`src/Scout.Host.Console/JobControlPoller.cs:176`). Without reviews in the host it is settled as not applied (`:174`).
  - `ControlAsync` pauses only a running review, continues only a paused one, stops either, and answers one sentence in each case (`src/Scout.Audit/RepositoryAudits.Reviews.cs:335`). After a stop it reads the review again and hands that row to the worker (`:336`).
- **The 60 minutes are a pause.** Before each wave, at 60 minutes of running time the job pauses with "it ran for 60 minutes" and one notice naming the batches left, and nothing is marked not read (`src/Scout.Audit/AuditWorker.Reviews.cs:82`). The running time is the clock time during which any batch ran since the last resume: the union of the finished batches' intervals that started after the job's `resumed` time (`src/Scout.Audit/SqliteAuditStore.cs:336`). A continue sets `resumed`, so it gives 60 more (`:188`). A wave begun before the bound runs to its end.
- **The stopped card, once.** A stop ends the job after the batches in progress. The worker posts the card of what was read, with the files not read "because the review was stopped", or one sentence when nothing was read, once per process (`src/Scout.Audit/AuditWorker.Reviews.cs:203`). A stopped review is handed to the worker through `AuditSignal` (`src/Scout.Audit/AuditWorker.cs:89`).
- **The desktop.**
  - One bell entry per pull request job, id `reviewjob:owner/repo#n` (`ui/packages/main/src/notifications/sources.ts:107`). It is not the mention review's `review:` entry, one turn the owner's mention starts, with no batches and no controls.
  - It shows the batches, files read and findings so far; at the end, the action `open-chat`.
  - The quiet chat: the progress, resumed and owner's pause lines never become chat turns (`ui/packages/main/src/notifications/sources.ts:234`, `ui/packages/main/src/quick-ask/quick-ask.ts:1965`). The start answer, a pause that needs the owner and the card always reach the chat.
  - The card shows the measured line, `N of M files read`, and the notes in place of "What Scout says it read". They are kept only from a block that arrived in a worker's notice (`ui/packages/renderer/src/app/core/review-findings.store.ts:501`, `ui/packages/renderer/src/app/core/review-findings.ts:190`). A reply is the model's turn, where the model or a pull request's text could write a count no code measured.

## Calls that leave the process

None new. The batch turns of ADR 0061 run up to `ReviewParallelBatches` at once. The control travels over the existing `scout:notifications.control`, `ControlJob`, `NextJobControl` and `SettleJobControl`, and writes the job's row in `audit.db` under the `github` feature's grant.

## What follows

- **Gained:** every file with a patch is read, sooner; one card per review in the chat; one click to control it.
- **Paid:** up to four turns at once against the provider's limits; the audit library reads the provider's name to choose its default ([[pages/roadmap/followups]]).
- **To watch:** the stopped-while-paused card is kept in memory; a restart before the next wake loses it.

## Rejected options

- **Restoring the bound of ten batches:** the files it dropped were the ones the owner most wanted read.
- **A hard cut at 60 minutes:** batches in flight would be lost half done, and the owner asked to be asked.
- **Counting an interrupted batch's time:** a batch a provider limit stopped has no end; counting it would charge time for nothing read.
- **A durable mark for the stopped-while-paused card:** a column and a migration for a window between a stop and the next wake.

## How it is held

- The order: `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:698`, `:769`. No bound: `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:702`. Files not listed: `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:61`, `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:98`, `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:366`.
- The waves: `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:738`, `:767`, `:781`, `:857`. The option: `tests/Scout.Audit.Tests/AuditServiceCollectionExtensionsTests.cs:65`, `:79`.
- The lines: `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:272`, `:883`, `:896`.
- The controls:
  - `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:815`, `:828`, `:844`, `:859`, `:894`;
  - `tests/Scout.ControlPlane.Tests/Controls/OwnerControlServiceTests.cs:54`, `:73`, `:91`;
  - `tests/Scout.Host.Console.Tests/JobControlPollerTests.cs:86`, `:99`, `:114`, `:324`;
  - `ui/packages/shared/test/ipc-contract.test.ts:334`, `ui/packages/main/test/notifications-job-controls.test.ts:1543`.
- The 60 minutes:
  - `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:198`, `:221`, `:243`;
  - `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:433`, `:454`, `:473`, `:484`.
  - No test holds that a wave begun before the bound ends.
- The stopped card: `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:816`, `:941`, `:960`, `:973`.
- The desktop: `ui/packages/main/test/notifications-sources.test.ts:587`, `:798`; `ui/packages/main/test/notifications-job-controls.test.ts:1486`, `:1507`, `:1523`, `:1555`; `ui/packages/main/test/quick-ask.test.ts:636`; `ui/packages/renderer/src/app/core/review-findings.spec.ts:457`, `:484`; `ui/packages/renderer/src/app/chat/chat.component.spec.ts:658`; `ui/packages/renderer/src/app/core/review-findings.component.spec.ts:2037`, `:2055`; `ui/packages/renderer/src/app/shell/notification-bell.component.spec.ts:625`, `:650`.
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/review-writes-on-its-pull-request]], [[pages/invariants/local-first-default]].

## Links

- Decisions it amends: [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]], [[pages/decisions/0061-the-pull-request-review-job-as-built]].
- How it works: [[pages/services/audit/how-it-works-review-job]].

## Amendments

2026-11-20: amended by [[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed]], every batch is also handed the inputs kept at the start.

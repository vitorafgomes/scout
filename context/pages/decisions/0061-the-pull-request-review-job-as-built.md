---
title: "ADR 0061 - The pull request review job as built"
version: "1.3"
date: 2026-11-18
updated: 2026-11-20
changes: "v1.3 (2026-11-20) dated line: amended by ADR 0064. v1.2 (2026-11-19) dated line: amended by ADR 0062"
page_type: adr
status: accepted
description: "The review job of ADR 0055 as built: a closed read-only batch turn that counts as read only when the model presented its block, the files read once and the head before each later batch, 10 batches and 60 minutes of running time, and one card that fits, built before the job is done, with the verdict and the read line computed by code."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Audit/AuditBatchRunner.cs
  - src/Scout.Audit/AuditWorker.Reviews.cs
  - src/Scout.Audit/SqliteAuditStore.cs
  - src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs
  - tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs
  - tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs
reliability: high
---

# ADR 0061 - The pull request review job as built

> The review job of [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]] is built: each batch is a closed read-only turn that counts as read only when the model presented its findings, the files are read once and the head before each later batch, only running time counts toward 60 minutes, and one card that fits ends the job, built before the job is marked done.

## Status

Accepted on 2026-11-18. It amends ADR 0055, whose text stays as it was decided; this record says how its third change was built and where the build differs from or sharpens that text. The owner decided on 2026-11-17 that only the time a review is running counts toward its 60 minutes, replacing the default of the wall clock from the start with pauses included, and approved a third round of fixes after a review that hunted for bugs.

Amended 2026-11-19 by [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]]: the bounds are replaced by a pause at 60 minutes of clock time since the last resume, and the batches run in waves, code first.

Amended 2026-11-20 by [[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed]]: a batch's request also carries the purpose, the project's rules and what was removed, each as data.

## The problem

ADR 0055 described the review job before it existed. Building it showed what the text left open: how often GitHub is read, what a batch whose model answered in prose counts as, how many findings fit on one card, what a failure while the card is built does to the job, and whether a paused review spends its 60 minutes.

## The decision

- **The batch turn** (`src/Scout.Audit/AuditBatchRunner.cs:112`) is offered the audit batch's five reads and `present_review_findings` only (`:67`), held to the job's repository and pull request (`:412`). Its request carries each file's patch as one line of JSON data (`:204`), and the titles of the job's findings so far, the most recent first and at most 100, on one more line of data, so a later batch is not told nothing (`src/Scout.Audit/AuditWorker.Reviews.cs:122`).
- **A batch reads only the job's repository.** A call to any offered read that names another repository, by its owner and repo or by the project of the Knowledge functions, is answered with one sentence and never runs; a read of the job's repository runs at any reference (`src/Scout.Audit/AuditBatchRunner.cs:616`). An audit batch is held the same way.
- **A batch is read only when the model presented it.** A turn that ends with no review block accepted for the job's repository, pull request and commit, answered in prose or naming another commit, is a failed attempt for "the AI model presented no findings for this batch" (`src/Scout.Audit/AuditBatchRunner.cs:118`, `:141`). It is tried once more like any failed turn; a second one leaves the batch not covered and no file read. An accepted empty list is a batch read with no findings (`src/Scout.Audit/FindingsBlock.cs:30`).
- **The worker's order.** A running review is picked before any audit, and an audit yields after its batch (`src/Scout.Audit/AuditWorker.cs:70`, `:105`).
- **GitHub is read once, then the head only.** ADR 0055 said "the head is read again before each batch". The changed files and their patches are read once each time the worker takes the review up, and that read's head serves the first batch; before every later batch only the head commit is read (`src/Scout.Audit/AuditWorker.Reviews.cs:78`). A batch is handed only its own files' patches (`:99`). A first read that already names another head than the job's is the moved head: the batch and every one after it are kept not read naming both commits. A job no longer running after the head read runs no turn (`:94`).
- **The bounds.** 10 batches, and 60 minutes of running time: the sum of the batches' turns, each from its start to its end as the batch rows keep them, so a paused time never counts and the sum survives a restart (`src/Scout.Audit/AuditWorker.Reviews.cs:151`, `src/Scout.Audit/SqliteAuditStore.cs:331`). This is the owner's decision of 2026-11-17.
- **The batch cap.** ADR 0055 said the plan is "sized by patch length". A batch is capped by the configured characters, 60,000 unless configured, each file sized by its entry as the request sends it (`src/Scout.Audit/AuditOptions.cs:22`). A cap from the model's context window is not built. A file whose patch alone passes the cap, and one whose path has a control character, are kept not read in batches of their own.
- **One card, verdict by code.** ADR 0055 said "the verdict and "read N of M files" computed by code". The verdict is one of three, from the kept findings' severities and whether every changed file was read: `ready after the blocking findings`, `ready to merge`, `no blocking findings in the files read` (`src/Scout.Audit/AuditWorker.Reviews.cs:14`, `:17`, `:20`, `:260`).
  - The card takes the most severe findings first while their JSON stays within 20,000 characters, at most 50, under the block the plugin accepts (`:32`, `:221`). A note counts the findings left off.
  - A card the function refuses, for the checks and the kept test run it adds, is presented again with half as many findings until a block is attached; the plain sentence is only for a function that refuses even an empty list (`src/Scout.Audit/AuditWorker.Reviews.cs:213`).
  - The card is built before the job is marked done, and posted only when the finish took (`:62`). A failure while it is built leaves the job running with every batch done, for the next wake.
- **From the chat.** `start_pull_request_review` and `get_pull_request_review_status` (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:28`, `:110`). The start is a declared write on the `github` feature's `Scout's data folder` row (`src/Scout.Plugin.GitHub/GitHubServiceCollectionExtensions.cs:155`, `src/Scout.ControlPlane/Features/FeatureCatalog.cs:98`); the status is a read in the chat's general set.

## Calls that leave the process

- The pull request with its changed files, once per run, and its head commit before each later batch: GitHub, read, through the plugin's `IGitHubClient`.
- The head commit's check runs and statuses, at the start: GitHub, read.
- The job's rows in `audit.db` and the snapshot: Scout's data folder, write, declared on the `github` feature and granted by the owner.
- The model, through Semantic Kernel. The posts, to the conversation the job holds. Nothing is written on GitHub.

## What follows

- **Gained:** a batch the model skipped never counts as read, so it cannot feed `ready to merge`; a large review's card always carries its most severe findings; a card that fails to build is built again; a review paused for hours resumes with its time left.
- **Paid:** one more turn for a batch whose first attempt presents nothing; the time between batches (the head read, the posts) does not count toward the 60 minutes.
- **To watch:** the card's budget sits under the plugin's block limit; a host test passes a card at the budget through the real function so the two cannot drift apart unseen.

## Rejected options

- **Read the whole pull request before each batch:** 1 + 30 reads per batch on a 3,000-file pull request, and one failed page pauses the review.
- **Measure the 60 minutes from the job's start:** a review paused for a provider limit would end on resume without reading anything more; rejected by the owner.
- **Count a batch read when its turn ends without error:** a model that answers in prose would mark every file read.
- **Mark the job done before building the card:** a failure in that window loses the card, and the job can never be started again at that commit.

## How it is held

- The batch turn:
  - `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:425`, the five reads and `present_review_findings` only;
  - `:404`, the patches as data;
  - `:495`, files read when the turn ends;
  - `:511`, a provider failure twice;
  - `:530`, a turn that presents no block, tried again and then not covered;
  - `:544`, an empty list read;
  - `:603`, another repository or pull request refused;
  - `:581`, `:597`, `:609`, the reads held to the job's repository in a review and an audit batch.
- The order: `tests/Scout.Audit.Tests/AuditWorkerTests.cs:1028`.
- The run:
  - `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:72`, a moved head;
  - `:96`, a first read at another head;
  - `:115`, the files read once and the head on later batches;
  - `:128`, a batch handed only its own patches;
  - `:147` and `:439`, a GitHub read that fails pauses;
  - `:162`, a stop during the head read runs no turn;
  - `:201`, the bounds;
  - `:223`, time paused does not count;
  - `:252`, one progress line per batch;
  - `:610`, `:622`, a later batch told the earlier titles, at most 100.
- The card:
  - `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:279`, `:309`, the verdict and the read line;
  - `:538`, batches that presented nothing are never ready to merge;
  - `:555`, a card that cannot be built leaves the review running;
  - `:579`, the findings that fit and the note;
  - `:637`, a card refused for its size presented again with fewer findings;
  - `:364`, never a block of kind `audit` and no declared write;
  - `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:398`, a card at the budget accepted by the real function; `:424`, with a kept test run and checks it still reaches the owner as a card.
- The store: `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:433`, running time from the batches.
- The start, the status and the host:
  - `tests/Scout.Plugin.GitHub.Tests/GitHubActionsPluginReviewJobTests.cs:44`, `:60`, `:76`, `:112`;
  - `tests/Scout.Host.Console.Tests/HostAuditTurnTests.cs:247`, `:266`, `:350`, `:375`.
- The skill, the declaration and the general set: `tests/Scout.Skills.Tests/BundledSkillsTests.cs:405`, `tests/Scout.ControlPlane.Tests/Features/FeatureCatalogTests.cs:368`, `tests/Scout.Host.Console.Tests/FunctionDeclarationTests.cs:86`.
- Invariants: [[pages/invariants/no-executive-writes]], [[pages/invariants/review-writes-on-its-pull-request]], [[pages/invariants/pluggable-boundaries]].

## Related

- Amends: [[pages/decisions/0055-pull-request-review-as-a-job-in-batches]], and through it [[pages/decisions/0054-functions-scoped-per-skill-and-job]] (the review batch's set and the two functions) and [[pages/decisions/0040-review-findings-as-cards-in-chat]] (the job's one card).
- How it works: [[pages/services/audit/how-it-works-review-job]], [[pages/services/plugin-github/the-audit-as-a-job]].

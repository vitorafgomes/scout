---
title: "Audit: Tests: Review job"
version: "1.0"
date: 2026-11-19
updated: 2026-11-19
changes: "v1.0: the review job's tests, moved from the Tests page, with the waves, the order of reading, the owner's controls and the pause at 60 minutes"
page_type: service
status: active
description: "Read when a change touches the tests of the pull request review job."
source: ["tests/Scout.Audit.Tests/RepositoryReviewsTests.cs", "tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs", "tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs", "tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs", "tests/Scout.Audit.Tests/AuditServiceCollectionExtensionsTests.cs", "context/pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell.md"]
reliability: high
---

# Audit: Tests: Review job

> The tests of the pull request review job in `tests/Scout.Audit.Tests`, as [[pages/services/audit/how-it-works-review-job]] describes the job and [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]] decides it. The project's other tests are on [[pages/services/audit/tests]].

## The start and the checks (`RepositoryReviewsTests.cs`)

- A review started at the head commit with its snapshot taken there and the answer naming every changed file, the batches, code first and the batches at a time; a plan by patch length with a file with no diff kept not covered; a refused snapshot returned as it is; a snapshot call that throws (`tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:144`); a snapshot at another commit than the head.
- The second start on a review running, done or paused at the same commit, stopped at it or done at an earlier one (5 cases); a start at a new head over a review at an earlier head, with one live review left (`:232`, 4 cases); nothing to read (`:255`); a pull request number of 0 or -1; a head that is not a commit SHA; a new review the database refuses after the earlier one was stopped (`:303`); a pull request with no diff left out; the status from the rows; an unopenable database; names that could leave the folder or end in a line break.
- The checks before a review: the size with its upper bound, waves of the batches at once, at most the 60 minutes (`:423`); Docker runs on and off (`:440`); the head's checks, kept once (`:462`); a refused snapshot as the grant block; a block that writes no row and returns every check (`:492`); warnings kept with the review; a switch that throws as `Could not check`; a start the database refuses (`:542`).
- The files GitHub did not list kept as one batch not read and counted in the start (`:98`); a count below zero taken as none (`:116`).
- A patch larger than one batch (`:584`), a control character in a path (`:599`), no file left to read (`:636`), a start or resume waking the worker (`:659`).
- The order of reading: code, then tests, then configuration, then documentation (`:698`), each path in its group (`:769`).
- The owner's controls: pause, continue and stop take only where they apply, each with its sentence (`:815`); a control the database refuses (`:828`); a bad name or number (`:894`); a stop of a review that paused meanwhile still handed to the worker (`:844`); a stop with no row read before it answers without throwing (`:859`); a stop whose read after it fails says it stopped and still hands the review over (`:873`).

## The run and the card (`AuditWorkerReviewTests.cs`)

- The head: a moved head (`tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:73`), a first read at another head (`:97`), the files read once and the head before later waves (`:116`), a batch handed its own patches (`:129`), a GitHub read failing (`:148`, `:480`), a stop during the head read (`:163`).
- The 60 minutes: a pause that asks with nothing marked not read (`:198`), 60 more after a continue (`:221`), time paused not counted (`:243`).
- To the end, in waves: 17 batches all run (`:702`); up to four at once, each saved as it ends (`:738`); one at a time in order (`:767`); a stop during a wave lets it end (`:781`); a provider limit in one batch saves the others and pauses once (`:857`); a provider limit at batch 2 and a resume (`:443`).
- The lines: one progress line per batch and no findings block (`:272`); none for a review started from another chat (`:883`); what the owner does posted once when it takes effect (`:896`).
- The stopped card: one card of what was read (`:941`); nothing read in one sentence (`:960`); a stopped paused review (`:973`); a pause then a stop during a batch, one card (`:816`).
- The card: the verdict and the read line (`:299`); a pull request GitHub listed in part never ready to merge, its note saying why and its progress line counting only the batches that can run (`:366`); tests not run and a partial reading (`:329`); the kept warnings (`:387`); never an `audit` block (`:405`); the code-review steps (`:429`); a database failure on a save (`:499`); a failing post (`:522`); a refused card as one sentence (`:539`); the 50 most severe (`:557`); batches presenting nothing never ready to merge (`:579`); a card not built leaving the review running (`:596`); the findings that fit (`:620`); earlier titles, at most 100 (`:651`, `:663`); a card refused for its size presented again with fewer (`:678`).

## The batch turn, the store and the option

- The review batch turn: patches as data, the five reads and `present_review_findings` only, its own block never an audit one, files read when the turn ends, reads of another repository refused and of its own at another reference run, a turn presenting no block retried then not covered, an empty list read, a provider failure twice, earlier titles as data, another repository or pull request refused (`tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:404` to `:671`).
- The store: a planned batch with a failure kept not covered (`tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:398`); the running time from the batches and never a pause (`:433`), from the last resume (`:454`), every batch for a file migrated before `resumed` existed (`:473`), batches that ran at once counted once (`:484`).
- The option: four at a time, one on the local provider unless set (`tests/Scout.Audit.Tests/AuditServiceCollectionExtensionsTests.cs:65`); outside 1 to 8 rejected at the host's start (`:79`).

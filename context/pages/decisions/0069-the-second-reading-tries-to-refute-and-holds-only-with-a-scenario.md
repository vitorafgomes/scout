---
title: "ADR 0069 - The second reading tries to refute, and holds a finding only with a scenario"
version: "1.2"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.2 (2026-11-21): a cited name resolved to one file before it is judged; the card names the findings not read again. v1.1 (2026-11-21): amended with the noted outcome and the check of a cited place. v1.0: the decision as first written"
page_type: adr
status: accepted
description: "Before the card, each finding is read again by a model turn told to refute it, offered three read-only functions held to the job's repository at the head; holds needs a concrete scenario, the severity follows a written rule in which block needs a named reason, and a failure of this reading never weakens or removes a finding."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/ContestRequest.cs
  - src/Scout.Review/Contestation.cs
  - src/Scout.Audit/AuditBatchRunner.cs
  - src/Scout.Audit/AuditWorker.Reviews.cs
reliability: high
---

# ADR 0069 - The second reading tries to refute, and holds a finding only with a scenario

> A finding reaches the card only if a reading told to disprove it could not, wrote the scenario in which it goes wrong, and named a reason for a block.

## Status

Accepted on 2026-11-21, built at the owner's request. It amends [[pages/decisions/0061-the-pull-request-review-job-as-built]], whose text stays as decided.

## The problem

Measured on real pull requests, many findings of the first reading were false, and many marked block were not blocking. A second reading that only assessed each finding held almost all of them: it checked that the code a finding describes was there, not that the harm it claims follows, and it did not look for a comment, a test or a page that says the behaviour is intended. Without tools it also answered uncertain for any claim whose proof was in a file it was not handed.

## The decision

- **It reads the head.** The turn is offered exactly `get_file_contents`, `search_code` and `list_repository_files` (`src/Scout.Audit/AuditBatchRunner.cs:759`), held to the job's repository by the same `HeldToJobRepository` filter a batch uses (`:977`). It is never offered a function that writes or presents findings, nor the project knowledge functions, whose index is of the local checkout, not the head. The request's first line names the repository and the head commit to read at (`src/Scout.Review/ContestRequest.cs:32`).
- **It tries to refute** (`ContestRequest.Instructions`, `:13`). For each finding it states the mechanism and the harm and tries to show the harm does not follow; before holding it looks for intent (a comment, a test that pins the behaviour, a page of the repository's documentation, found with `search_code` at the reference), and behaviour documented or tested as intended is refuted; a finding about a test is checked by reading the whole assertion and the code under test; uncertain is only for a claim that cannot be decided after reading, never for code not supplied.
- **Holds needs a scenario.** The answer is one JSON array; a finding that holds adds `scenario`, `severity` and `because` (`src/Scout.Review/Contestation.cs:116`). A holds without a scenario is read as uncertain by code ("Scout's second reading said this finding holds but wrote no scenario in which it goes wrong.", `:100`). A reason or a scenario over 300 characters is cut on a word and kept; an entry with no id, an unknown or missing status, no reason, or that is not an object, is uncertain.
- **The severity rule** (`SeverityRule`, `:47`), written once and given to the model in its own words: block only when the scenario ends in lost or corrupted data, a security or secret exposure, a build or a test run that fails, or a core operation that cannot complete; fix for a wrong result or a missing protection; nit for wording, documentation or style. Code applies it: a held finding takes the concluded severity; a block without one of the named reasons is a fix, and a held block whose answer gives no known severity is capped at fix (`:195`). The finding's why ends with "Scenario: ..." within its 4,000-character bound, the why before it cut first (`:215`). The card counts the severities the reading changed.
- **Uncertain** keeps the finding as unconfirmed: its title starts "Unconfirmed: " and it never blocks.
- **Bounds.** Groups of at most 4 findings and 15 groups (`:82`, `:85`). Each group has its own time, the batch turn's timeout, inside 30 minutes for the whole reading (`:88`, `src/Scout.Audit/AuditWorker.Reviews.cs:610`). A group that runs out of its time, fails or answers unreadably leaves its findings as the first reading gave them and the card says it did not run; the next group still runs.
- **Kept across a restart.** A held outcome writes its severity and its why with the scenario to the stored finding, so a review that continues shows what this reading concluded.

## Calls that leave the process

One model turn per group, through the same chat service as the batches, and the reads the turn makes: GitHub reads of the job's repository, or local reads of its snapshot. No write.

## What follows

- **Gained:** measured on real pull requests, the share of false findings held fell sharply once the reading was told to refute, and no finding is held as blocking without a named reason.
- **Paid:** each group takes longer, since it reads.
- **To watch:** a claim whose proof is in the pull request's description rather than in the code is still decided unevenly from one run to the next ([[pages/roadmap/followups]]).

## Rejected options

- **A second reading that only assesses:** it held false findings.
- **Keep the first reading's severity:** too many findings were marked block without a blocking harm.

## How it is held

- The offered set and the filter: `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:1596`. A failure leaves every finding: `:1667`. A group out of time, the next still runs: `:1740`. The severity and the scenario on the card: `:1721`.
- The instructions: `tests/Scout.Review.Tests/ContestRequestTests.cs:26`, `:40`. A holds without a scenario: `tests/Scout.Review.Tests/ContestationTests.cs:88`. The severity rule: `:107`, `:118`. Long answers kept: `:65`.

## Links

- Projects it shapes: [[pages/services/review]], [[pages/services/audit]]
- Decisions it amends: [[pages/decisions/0061-the-pull-request-review-job-as-built]]

## Amendments

### 2026-11-21: a fourth outcome, noted, and the cited place checked

Seen on a repository Scout had never reviewed: the second reading refuted three findings because the repository's own page recorded each as later work. All three were true in the code, so the card said nothing to the author; two of the three cited lines of that page were wrong.

- **Noted** (`src/Scout.Review/Contestation.cs:209`). A finding whose claim is true and which the project records as known or later work (a followups or roadmap page, a list of known weak points, a TODO the change adds knowingly, or a known weak point the pull request's own description lists) is answered `noted`. Code keeps it as a nit whatever its severity, titles it "Noted: " (`NotedTitle`, `:326`; a first reading's "Noted:" is stripped like "Unconfirmed:", and the card puts the prefix back only on the findings the second reading noted, `src/Scout.Audit/FindingsBlock.cs:157`), and ends its why with "Recorded by the project: " and the reason. Refuted stays for a claim that is false or for behaviour that is intended and correct as designed. The card's note counts the notes. A note is a nit and is not unconfirmed, so it never counts for request changes; a review with only notes offers a comment that counts them as optional suggestions. The card selects nothing by severity, so a note is decided item by item as any nit.
- **What writing down does not excuse** (`src/Scout.Review/ContestRequest.cs:19`). A recorded shortcoming that silently loses or corrupts data, or exposes a secret, holds at its severity with the record cited. Code cannot check that; the instructions and a test of their text carry it.
- **The cited place** (`Contestation.Checked`, `src/Scout.Review/Contestation.cs:356`; read in the worker by `Checked`, `src/Scout.Audit/AuditWorker.Reviews.cs:768`). A refutation or a note whose reason cites a file and line (`path:N`, `path#LN`, `path line N`) that the job's snapshot does not hold, a missing file or a file with fewer lines, is uncertain. Only that the place exists is checked, never what it says. Each cited file is read once per answer from the snapshot, by the same reader the code around a finding uses, within the snapshot and at most 1 MB; with no snapshot nothing is checked.
- **Stored.** `noted` is a state of its own in the findings' text column, with its reason; no migration. The turn table's counts of holds, refuted and uncertain gain no noted column: it would need a migration, and was left.
- **Held by** `tests/Scout.Review.Tests/ContestationTests.cs:228` to `:287`, `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:1705`, `:1724` and `:1735`, and `tests/Scout.Audit.Tests/FindingsBlockTests.cs:79`.

### 2026-11-21: a cited name is resolved to one file before it is judged

Seen on real pull requests: nine refutations and notes were made uncertain because they cited a file by its name alone, often the finding's own file at the finding's own line; the check looked for that name as a path from the root and called it unreal. The check above is replaced as follows.

- **Resolving the name** (`Contestation.Resolve`, `src/Scout.Review/Contestation.cs:380`), in this order: the name as a path from the root, when the snapshot holds it; the finding's own path, when the name is that path or a trailing part of it at a folder boundary; the one changed file of the review whose path ends with the name at a folder boundary. The snapshot itself is not listed: no reader of it lists the tree cheaply, so resolution stops at the changed files.
- **Judging** (`Contestation.Checked`, `:357`; the worker's `Checked`, `src/Scout.Audit/AuditWorker.Reviews.cs:770`, handed the finding's path and the files of the review's batches). Only a citation that resolves to one file is judged, and it is unreal only when that file has fewer lines than the line cited; the reason to the owner names the file as resolved. A name that resolves to no file or to more than one is no evidence, and the outcome stands as the model gave it. A path with folders that resolves to nothing stands too: without a listing of the snapshot it may still be a real file elsewhere in the tree, and the check must never destroy a good reading on a guess.
- **The instructions** ask for the file by its path from the repository's root, in the reason and in the scenario, so most citations resolve at the first step. A long path takes room in the 300 characters of a reason; a reason past them is cut on a word and its entry stays valid, and a citation the cut breaks is not read as one, so the outcome stands.
- **The findings not read again.** A finding the second reading gave no readable answer for stays as the first reading gave it, as before; the card's note on it now names the findings after the reason ("Kept as the first reading gave it: <title>"), within the note's bound.
- **Held by** `tests/Scout.Review.Tests/ContestationTests.cs:290` and `:304`, and `tests/Scout.Audit.Tests/AuditWorkerReviewTests.cs:1731` and `:1742`.


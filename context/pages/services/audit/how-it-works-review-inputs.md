---
title: "Audit: How it works: Review inputs"
version: "1.2"
date: 2026-11-20
updated: 2026-11-20
changes: "v1.2 (2026-11-20): no start without room; the size from the final plan. v1.1 (2026-11-20): the constant block first, the plan in the room the inputs leave, rule files read only while they can be kept. v1.0: the purpose, the rule files and what was removed, kept at a review's start and handed to every batch."
page_type: service
status: active
description: "Read when a change touches: Review inputs; the purpose; the rule files; what was removed; the bounds of a batch's request."
source: ["src/Scout.Audit/ReviewInputs.cs", "src/Scout.Audit/AuditBatchRunner.cs", "src/Scout.Audit/AuditWorker.Reviews.cs", "src/Scout.Audit/RepositoryAudits.Reviews.cs", "src/Scout.Audit/SqliteAuditStore.cs", "src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs", "src/Scout.Host.Console/PullRequestReviewsAdapter.cs"]
reliability: high
---

# Audit: How it works: Review inputs

> A pull request review keeps three inputs from its start, what the pull request says it does, the project's own rule files and what the pull request removed, and hands them to every batch as data, each bounded by the length it is sent at ([[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed]]).

## At the start

`start_pull_request_review` builds the purpose from the pull request it already read, its title and description, and reads the issue its description names, at most once: a closing keyword first, else the first `#n`, never the pull request's own number (`src/Scout.Plugin.GitHub/GitHubActionsPlugin.Reviews.cs:73`, `:90`). An issue GitHub fails or does not find is marked unread; an answer that is a pull request is no issue. The host copies the purpose and each file's status into the audit library's types (`src/Scout.Host.Console/PullRequestReviewsAdapter.cs`).

Once the snapshot is found at the head, the start builds the inputs (`src/Scout.Audit/RepositoryAudits.Reviews.cs:160`):

- **The purpose,** cut by `ReviewInputs.Bound`: a title and the issue's title at 300 characters, a description and the issue's body at 4,000, each body's cut flag set when it was cut (`src/Scout.Audit/ReviewInputs.cs:264`).
- **The rules,** read by `ReviewRules.Read` (`ReviewInputs.cs:44`) from the snapshot in a fixed order, a file read only while its text can still be kept: `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`, `.github/CONTRIBUTING.md` and `docs/CONTRIBUTING.md` whatever their case, then the `*.md` files directly inside `.claude/rules`, `docs/conventions`, `docs/rules`, `context/pages/invariants` and `context/pages/conventions`. A file is cut at 6,000 characters; past 12 files or 16,000 characters in total, over 1 MB or holding a NUL, it is not included, at most 20 by path and the rest counted (`:25`-`:28`). A file the pull request changes is marked `changedHere`. A failed read answers no file, marked unreadable.
- **What was removed,** by `ReviewRemoved.From` (`ReviewInputs.cs:162`): a deleted file, a deleted test file, a test file emptied by its one hunk, and a test file whose patch removes more test declarations than it adds, test entries first, at most 200 with the rest counted, and the counts kept over all of them. For a deleted or emptied test file, up to three code files of the same stem still in the snapshot. A changed test file with no diff is counted as not examined; a snapshot that cannot be listed marks the repository not searched.

The inputs are kept on the job row, in the column `inputs` (migration `20261119120000_ReviewInputs`), in the same save as the row. A resumed review keeps the inputs it started with.

## In each batch

The worker reads the inputs once per run (`src/Scout.Audit/AuditWorker.Reviews.cs:62`), builds the job's constant block once (`:63`, `src/Scout.Audit/AuditBatchRunner.cs:279`) and hands both to every batch of every wave; a read the database refuses leaves the review running for the next wake, and no batch runs without them. The request opens with the constant block, the same text for every batch, each part under its own sentence (`AuditBatchRunner.cs:63`, `:68`) as one line of JSON:

- the purpose, or "The pull request's title and description were not given to this review.";
- the rule files with `notIncluded`, then "N more rule files did not fit and are not named." when some were counted; or "The repository has none of the rule files Scout looks for at the reviewed commit."; or "The project's rule files could not be read; judge against the conventions profile and what the files show.".

Then come the batch's header, the scope sentence (`:57`), Scout's counts of what was removed, one sentence each, and the removals of the batch's own files and of the code they tested under their sentence (`:75`), then "N more removals related to these files did not fit and are not listed." when some were dropped.

The start plans the batches in the room the inputs leave under `MaxBatchCharacters` (`src/Scout.Audit/RepositoryAudits.Reviews.cs:164`, measured by `AuditBatchRunner.cs:322`); a patch with no room beside them is not read, as a diff larger than one batch, and when none has room the review does not start and no job is kept (`RepositoryAudits.Reviews.cs:166`). The size check and the start sentence name the batches of that final plan. Inputs that hold nothing to hand are not kept.

A job that kept no inputs is asked as before, but for the scope sentence.

## Bounds as sent

Each line is bounded by its length as sent, since a quote or backslash takes two characters on the line and a control character six: the purpose at 10,000, the rules at 18,000, the removals at 4,000 (`AuditBatchRunner.cs:261`, `:264`, `:267`). A line over its bound is made smaller and serialized again until it fits (`:357`): the longer body is cut and marked, a rule file's text is cut and marked `cut`, then a path not included and a rule file are dropped and counted, and a removal is dropped and counted. With every input at its bound, the inputs take at most 34,500 characters, and the plan leaves them that room.

## On the card

The card's notes add Scout's sentences on the inputs after the checks' warnings (`ReviewInputs.cs:282`): the files deleted and how many were test files, the test files that leave code named like them, those emptied, those losing test declarations, those with no diff, a repository not searched, an issue not read, rule files not read. Each is counted over every removal, agrees with its number and names no path or word of the pull request.

## What the counts cannot see

A test declaration is counted from patch lines with one pattern (`ReviewInputs.cs:149`) and a test file is matched to its code by name (`:216`): a declaration split over lines or a framework the pattern does not name is missed, a test moved to another file counts as removed, and a namesake elsewhere counts as code left behind. The batch is told so.

---
title: "ADR 0067 - A file too large for one batch is read in parts"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A changed file whose diff does not fit one batch is cut at hunk boundaries into at most 10 parts, each a batch of its own planned with the other files of its reading order, parts in order; a part keeps the whole file's line numbers and its batch is told which new-file lines it covers."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/PatchParts.cs
  - src/Scout.Audit/RepositoryAudits.Reviews.cs
  - src/Scout.Audit/AuditBatchRunner.cs
reliability: high
---

# ADR 0067 - A file too large for one batch is read in parts

> The most changed file of a pull request is read like any other, in parts that each fit one batch, in the plan's code-first order.

## Status

Accepted on 2026-11-21, built at the owner's request after measuring on real pull requests that the problems missed sat in the file too large for a batch. It amends [[pages/decisions/0061-the-pull-request-review-job-as-built]] and [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]], whose texts stay as decided.

## The problem

A file whose diff was larger than one batch's room was not read: it was counted as not covered. Measured on real pull requests, that file was often the one that held the problems a careful review finds.

## The decision

- **The cut.** `PatchParts.Split` (`src/Scout.Review/PatchParts.cs:31`) cuts a patch at hunk boundaries into parts that each fit the room a batch has beside its inputs; a patch with no hunk header is one part with a problem (`:24`). A part's hunk headers keep the whole file's numbers, so a quote in a part derives the same line as in the whole patch ([[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]]).
- **At most 10 parts** per file (`src/Scout.Audit/RepositoryAudits.Reviews.cs:69`); the parts past the bound are one batch kept not covered, which says how many were not read.
- **The order.** The plan groups the changed files by their reading order (code, tests, build and configuration, documentation) and, after each group's batches, plans the parts of that group's files too large for one batch, by path, parts in order (`:290`). A large code file is read with the code, before the tests and the documentation. A job planned before this keeps its stored order: the plan is written once at the start and never planned again on resume.
- **The batch is told** which part it reads and which new-file lines the part covers, that those lines can be read in the file with `get_file_contents` and that first line, and that it reports findings only on its own lines (`src/Scout.Audit/AuditBatchRunner.cs:528`).

## Calls that leave the process

None. The parts come from the pull request's files already read at the start.

## What follows

- **Gained:** a large file is read to its end.
- **Paid:** more batches, so more model turns.
- **To watch:** a finding that needs two parts of the same file at once is seen by neither part alone.

## Rejected options

- **Leave a large file not covered:** it is where the problems were.
- **Plan the parts after every other batch:** the most changed code file was read after all the documentation.

## How it is held

- `tests/Scout.Audit.Tests/RepositoryReviewsTests.cs:672` (a large file planned as parts, no changed line left out) and `:692` (parts with the code, before the documentation, in order).
- `tests/Scout.Review.Tests/RemovedEvidenceTests.cs:28` (a part keeps the whole file's numbers).

## Links

- Projects it shapes: [[pages/services/audit]], [[pages/services/review]]
- Decisions it amends: [[pages/decisions/0061-the-pull-request-review-job-as-built]], [[pages/decisions/0062-the-review-job-runs-to-its-end-in-parallel-from-the-bell]]

## Amendments

None yet.

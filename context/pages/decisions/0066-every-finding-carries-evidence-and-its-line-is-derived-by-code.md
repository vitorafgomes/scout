---
title: "ADR 0066 - Every finding carries evidence, and its line is derived by code"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the decision as first written"
page_type: adr
status: accepted
description: "A review finding quotes the patch of its file, on the new side or among the removed lines, and Scout derives its line from the quote by a fixed order of steps that never guesses: a quote found more than once is refused with its count; a finding the quote cannot place is asked for once more per reading and, if it still fails, left out and named."
supersedes: ""
superseded_by: ""
source:
  - chat
  - src/Scout.Review/DiffQuote.cs
  - src/Scout.Audit/AuditBatchRunner.cs
  - src/Scout.Audit/SqliteAuditStore.cs
reliability: high
---

# ADR 0066 - Every finding carries evidence, and its line is derived by code

> The model quotes the code a finding is about; Scout finds the quote in the patch and gives the finding its line. A finding whose quote does not place it is asked for once more, and is otherwise left out and named, never put on a guessed line.

## Status

Accepted on 2026-11-21, built at the owner's request after measuring on real pull requests that the line a model gives is often wrong. It amends [[pages/decisions/0061-the-pull-request-review-job-as-built]], whose text stays as decided. The rule lives in Scout.Review ([[pages/decisions/0065-the-review-rules-live-in-scout-review]]).

## The problem

A finding carried the line the model said it was on. Measured on real pull requests, many of those lines pointed at the wrong code or outside the diff, so the comment on GitHub sat under the wrong line or was dropped. A finding about removed code, such as a deleted test, had no new-side line to point at at all.

## The decision

- **The evidence rule.** Every finding carries `evidence`: an exact, contiguous quote of its file's patch, the smallest that proves it, never a blank line, a comment or an import alone (`src/Scout.Audit/AuditBatchRunner.cs:387`). A finding about removed lines quotes those lines without the leading `-`. A finding on a file the pull request deletes needs none and sits on line 1. The line the model gives is not used.
- **The match** (`DiffQuote.Locate`, `src/Scout.Review/DiffQuote.cs:40`). Each line of the quote and of the patch is compared with its ends trimmed and each run of spaces or tabs read as one space (`:119`). The steps run in this order, each on the new side first and then on the removed lines; the first step that finds the quote decides:
  1. the quote's lines as adjacent lines;
  2. the same, with blank lines left out of the quote and of the patch;
  3. on the removed lines only, the quote's lines in order inside one hunk, with other lines between them (`:63`);
  4. the quote's first line of code alone, as a whole line and then as a part of one line.
  Found once, the finding is placed on its first line of code; found more than once, it is refused with the count ("has evidence found N times in its file's patch."); found nowhere, it is not found. A quote that is only blank lines, comments or directives is refused as such.
- **The removed side's line.** A quote of removed lines is placed where the removal happened, computed from the hunk (`DiffQuote.RemovedLines`, `:180`): the first new-side line after the removed run, else the last one before it, else the hunk's new start line, at least 1. A file header before the first hunk is not a removed line.
- **One more request.** When an answer has findings Scout could not place (no evidence, not found, found more than once, or a line off the diff on a deleted file), the batch is asked once more with each such finding's id, title, path and problem as one line of JSON data, followed by the evidence rule (`AuditBatchRunner.cs:391`). This extra request is its own budget, one per reading, so a reading takes at most three turns. If the extra request throws, times out or presents nothing, the first answer's placed findings and its left-out sentence are kept, and the batch counts as read.
- **What still fails** is left out and named in the batch's sentence ("1 finding left out: it has evidence not found in its file's patch."), never placed on a guessed line.
- **The same finding twice.** The store's merge key reads a title by its letters and digits only, upper-cased (`src/Scout.Audit/SqliteAuditStore.cs:299`), so two findings on one path whose titles differ only in case, spacing or punctuation are one.

## Calls that leave the process

None. The extra request is one more model turn of the same batch, under the same functions and filters.

## What follows

- **Gained:** a finding's comment sits on the line its quote proves; a finding about a deleted test reaches the card.
- **Paid:** up to one more model turn per reading; a finding without a usable quote is lost after that turn.
- **To watch:** a quote longer than its line, such as one with a trailing comma the line does not have, is not found; only a quote shorter than its line is forgiven, as a part of it. A hunk that only removes gives a line that may not be on the new side, so its inline comment is dropped and the finding stays in the note's body ([[pages/roadmap/followups]]).

## Rejected options

- **Trust the model's line:** measured wrong too often.
- **Place a quote found more than once on its first occurrence:** a guess; the count is the refusal.
- **A new contract field for removed-side evidence:** the removed side is found by the same quote, so no field was needed.

## How it is held

- The match: `tests/Scout.Review.Tests/QuoteMatchingTests.cs:32`, `:38`, `:47`; the removed side: `tests/Scout.Review.Tests/RemovedEvidenceTests.cs:24`, `:28`, `:51`.
- The extra request: `tests/Scout.Audit.Tests/AuditBatchRunnerTests.cs:1037` (asked once, then left out and named), `:1084` (a finding with no evidence asked for once more after a failed attempt), `:1126` (what the request names), `:1067` (an extra request that does not complete keeps the first answer).
- The merge key: `tests/Scout.Audit.Tests/SqliteAuditStoreTests.cs:31`.

## Links

- Projects it shapes: [[pages/services/review]], [[pages/services/audit]]
- Decisions it amends: [[pages/decisions/0061-the-pull-request-review-job-as-built]]

## Amendments

None yet.

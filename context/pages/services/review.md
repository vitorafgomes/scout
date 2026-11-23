---
title: "Review"
version: "1.0"
date: 2026-11-21
updated: 2026-11-21
changes: "v1.0: the page as first written"
page_type: service
status: active
description: "Scout.Review holds the rules of a pull request review and nothing else: the finding and its limits, the diff's lines, the evidence match, the parts of a large patch, the focused readings, the second reading's request and answer, the severity rule and the one offer; it references no project and does no I/O."
source: ["src/Scout.Review/", "tests/Scout.Review.Tests/", "context/pages/decisions/0065-the-review-rules-live-in-scout-review.md"]
reliability: high
---

# Review

> Every rule a pull request review applies is written once, in `src/Scout.Review`, and the audit job and the GitHub plugin call it ([[pages/decisions/0065-the-review-rules-live-in-scout-review]]).

## Responsibility

Owns the rules of a review as pure code: no file, no network, no model, no store. Its project file references no other project and no package (`src/Scout.Review/Scout.Review.csproj`). It is used by [[pages/services/audit]] and [[pages/services/plugin-github]] and by nothing else. The control plane still checks a findings card's shape only, not these rules ([[pages/roadmap/followups]]).

## What it holds

| Type | What it decides | Record |
|---|---|---|
| `Finding`, `FindingLimits`, `FindingValidation` | A finding's fields, severities and bounds, and why one is refused | [[pages/decisions/0063-what-scout-writes-on-a-pull-request-follows-the-templates]] |
| `DiffLines` | The new side of a patch, the lines a comment may sit on, the hunk around a line, kept around the quote a finding gives (`src/Scout.Review/DiffLines.cs:28`) | [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]] |
| `DiffQuote` | Where a quote sits in a patch, by its order of steps, on the new side or among the removed lines (`src/Scout.Review/DiffQuote.cs:40`) | [[pages/decisions/0066-every-finding-carries-evidence-and-its-line-is-derived-by-code]] |
| `PatchParts` | A patch cut at hunk boundaries into parts that keep the whole file's numbers (`src/Scout.Review/PatchParts.cs:31`) | [[pages/decisions/0067-a-file-too-large-for-one-batch-is-read-in-parts]] |
| `ReviewReadings` | The reading order of a file, which files are code or tests, and the focused readings a batch gets (`src/Scout.Review/ReviewReadings.cs:35`, `:59`) | [[pages/decisions/0068-focused-readings-only-on-code-and-test-batches]] |
| `PullRequestDescription` | The requests a description lists, read in linear time | [[pages/decisions/0064-the-review-batch-is-handed-the-purpose-the-rules-and-what-was-removed]] |
| `TestRunFacts` | What a test run established, as data (`src/Scout.Review/TestRunFacts.cs:21`) | [[pages/decisions/0060-a-run-that-could-not-run-its-tests-is-not-a-failed-run]] |
| `CalledNames` | The names a quote calls, for the definitions handed to the second reading | [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]] |
| `ContestRequest`, `Contestation`, `SeverityRule` | The second reading's instructions and request, how its answer is read and applied, the severity rule and the card's notes on it (`src/Scout.Review/ContestRequest.cs:13`, `src/Scout.Review/Contestation.cs:47`, `:116`, `:195`) | [[pages/decisions/0069-the-second-reading-tries-to-refute-and-holds-only-with-a-scenario]] |
| `ReviewOffers` | The one offer of a review (`src/Scout.Review/ReviewOffer.cs:61`) | [[pages/decisions/0070-one-verdict-decision-and-no-approval-on-its-own]] |

## Rules

- A rule a second caller needs moves here; it is never copied ([[pages/decisions/0065-the-review-rules-live-in-scout-review]]).
- Nothing here reads a file or calls out: the callers hand it what they read.
- Every pattern that runs on text from a pull request or a model is linear or bounded by a match timeout.

## Tests

`tests/Scout.Review.Tests` holds one class per rule:

| Class | What it holds |
|---|---|
| `DiffQuoteTests`, `QuoteMatchingTests`, `RemovedEvidenceTests` | The match's steps, removed-side evidence and its line, the hunk handed to the second reading (`tests/Scout.Review.Tests/QuoteMatchingTests.cs:32`, `tests/Scout.Review.Tests/RemovedEvidenceTests.cs:24`) |
| `DiffLinesTests`, `PatchPartsTests` | The new side and the parts |
| `ReviewReadingsTests` | The focused readings and the reading order (`tests/Scout.Review.Tests/ReviewReadingsTests.cs:37`, `:61`) |
| `ContestRequestTests`, `ContestationTests` | The second reading's instructions, the answer, the scenario and the severity rule (`tests/Scout.Review.Tests/ContestRequestTests.cs:26`, `tests/Scout.Review.Tests/ContestationTests.cs:88`, `:107`) |
| `ReviewOfferTests` | The one offer and that approval is never offered on its own (`tests/Scout.Review.Tests/ReviewOfferTests.cs:117`) |
| `FindingLimitsTests`, `FindingValidationTests`, `CalledNamesTests`, `PullRequestDescriptionTests` | The finding's bounds, the names a quote calls, the description's requests |

## History

What changed and when: [[pages/history/review]].
